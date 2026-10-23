using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stransfer.Core;

public sealed partial class TransferManager
{
    private async Task TransferWithRetryAsync(JobState state, FilePlan file, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await TransferFileAsync(state, file, token).ConfigureAwait(false); return; }
            catch (IOException error) when (attempt < 6 && ((error.HResult & 0xffff) is 32 or 33
                || error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 }))
            {
                // Recently terminated processes and scanners can retain a file briefly. Every retry revalidates it.
                await Task.Delay(Math.Min(50 << attempt, 800), token).ConfigureAwait(false);
            }
        }
    }

    private async Task TransferFileAsync(JobState state, FilePlan file, CancellationToken token)
    {
        EnsureOriginalVolumesPresent(state);
        state.DirectoryGuard!.CheckFile(file.Destination);
        state.DirectoryGuard.CheckFile(file.Partial);
        state.DirectoryGuard.CheckFile(file.Source);
        if (file.ReadyToCommit && File.Exists(file.Destination))
        {
            await RecoverCommittedFileAsync(state, file, token).ConfigureAwait(false);
            return;
        }
        if (File.Exists(file.Destination) || Directory.Exists(file.Destination))
            throw new IOException($"The planned destination is now occupied; it was kept unchanged: {file.Destination}");
        ValidateSource(file);
        if (OperatingSystem.IsWindows() && state.Operation == TransferOperation.Move && file.Offset == 0 && !File.Exists(file.Partial)
            && SameVolume(state, file.Source, file.Destination))
        {
            if (await TryAtomicMoveAsync(state, file, token).ConfigureAwait(false)) return;
        }
        if (state.Operation == TransferOperation.Move) VolumeIdentity.RejectNamedStreamsForMove(file.Source);
        var bufferSize = file.Length >= 512L * 1024 * 1024 ? 4 * 1024 * 1024 : (int)Math.Clamp(file.Length, 64 * 1024, 1024 * 1024);
        if (OperatingSystem.IsWindows() && !state.Verify && state.Operation == TransferOperation.Copy && file.Length >= 8L * 1024 * 1024
            && file.Offset == 0 && !File.Exists(file.Partial))
            await Task.Run(() => NativeCopy(state, file, token), token).ConfigureAwait(false);
        else
        {
        // These streams already receive pooled buffers. Disable FileStream's additional per-file buffers,
        // which otherwise allocate megabytes for a tiny file and duplicate the copy pipeline.
        await using (var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(file.Partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var sourceHasher = state.Verify || state.Operation == TransferOperation.Move ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            ValidateSource(file);
            if (destination.Length < file.Offset) SetOffset(state, file, 0);
            if (file.Offset > 0)
                await ValidatePrefixAsync(source, destination, file.Offset, bufferSize, token, sourceHasher).ConfigureAwait(false);
            destination.SetLength(file.Offset);
            source.Position = destination.Position = file.Offset;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            var nextBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            Task<int>? pendingRead = null;
            var timer = Stopwatch.StartNew();
            var checkpoint = file.Offset;
            try
            {
                if (file.Offset < file.Length)
                    pendingRead = source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(bufferSize, file.Length - file.Offset)), token).AsTask();
                while (pendingRead is not null)
                {
                    token.ThrowIfCancellationRequested();
                    var read = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (read == 0) throw new IOException("The source ended before its recorded length; it may have changed.");
                    var remainingAfterWrite = file.Length - file.Offset - read;
                    if (remainingAfterWrite > 0)
                        pendingRead = source.ReadAsync(nextBuffer.AsMemory(0, (int)Math.Min(bufferSize, remainingAfterWrite)), token).AsTask();
                    // Complete each chunk without cancellation. A checkpoint never includes an incomplete write.
                    await destination.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                    sourceHasher?.AppendData(buffer, 0, read);
                    (buffer, nextBuffer) = (nextBuffer, buffer);
                    SetOffset(state, file, file.Offset + read);
                    Publish(state);
                    if (file.Offset - checkpoint >= (checkpoint == 0 ? 32L : 256L) * 1024 * 1024 || timer.ElapsedMilliseconds >= 1000)
                    {
                        DurableCheckpoint(state, file, destination);
                        checkpoint = file.Offset;
                        timer.Restart();
                    }
                }
                token.ThrowIfCancellationRequested();
                destination.Flush(flushToDisk: true);
                lock (state.Sync) file.DurableOffset = file.Offset;
                ValidateSource(file);
                if (state.Verify || state.Operation == TransferOperation.Move)
                {
                    destination.Position = 0;
                    var sourceHash = sourceHasher!.GetHashAndReset();
                    var destinationHash = await HashStreamAsync(destination, token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                        throw new IOException("SHA-256 verification failed. The source was kept unchanged.");
                    lock (state.Sync) file.VerifiedHash = Convert.ToHexString(sourceHash);
                }
                ValidateSource(file);
                lock (state.Sync) file.ReadyToCommit = true;
                await SaveFileAsync(state, file).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DurableCheckpoint(state, file, destination);
                throw;
            }
            finally
            {
                // A read-ahead operation must release its rented buffer before either buffer is returned.
                if (pendingRead is not null) { try { await pendingRead.ConfigureAwait(false); } catch { /* Preserve the original copy failure. */ } }
                ArrayPool<byte>.Shared.Return(buffer); ArrayPool<byte>.Shared.Return(nextBuffer);
            }
        }
        }
        token.ThrowIfCancellationRequested();
        EnsureOriginalVolumesPresent(state);
        File.SetLastWriteTimeUtc(file.Partial, new DateTime(file.LastWriteTicks, DateTimeKind.Utc));
        File.Move(file.Partial, file.Destination); // Same-directory atomic publish; never overwrites another file.
        lock (state.Sync) file.DestinationCommitted = true;
        ApplyMetadata(file);
        if (state.Operation == TransferOperation.Move)
            await DeleteSourceAfterVerificationAsync(file, token).ConfigureAwait(false);
        await MarkCompletedAsync(state, file).ConfigureAwait(false);
    }

    private async Task<bool> TryAtomicMoveAsync(JobState state, FilePlan file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using (var source = VolumeIdentity.OpenForVerifiedDelete(file.Source, 1024 * 1024))
        {
            ValidateSource(file);
            lock (state.Sync) file.SourceFileIdentity = VolumeIdentity.FileIdentity(source);
            // Filesystems without stable file IDs use the regular verified copy/delete path.
            if (file.SourceFileIdentity is null) return false;
            lock (state.Sync) { file.AtomicMove = true; file.ReadyToCommit = true; }
            await SaveFileAsync(state, file).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateSource(file);
            VolumeIdentity.RenameSource(source, file.Destination);
            SetOffset(state, file, file.Length);
            lock (state.Sync) { file.DestinationCommitted = true; file.DurableOffset = file.Length; }
        }
        await RecoverCommittedFileAsync(state, file, token).ConfigureAwait(false);
        return true;
    }

    private async Task RecoverCommittedFileAsync(JobState state, FilePlan file, CancellationToken token)
    {
        RejectReparse(file.Destination);
        var destinationInfo = new FileInfo(file.Destination);
        if (destinationInfo.Length != file.Length) throw new IOException("A published destination changed. The remaining source was kept.");
        if (file.AtomicMove)
        {
            using var destination = File.Open(file.Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.SourceFileIdentity is null || VolumeIdentity.FileIdentity(destination) != file.SourceFileIdentity)
                throw new IOException("The destination no longer has the identity of the atomically moved file.");
            if (File.Exists(file.Source)) throw new IOException("A new file exists at the original source path; it was kept unchanged.");
        }
        else
        {
            if (file.VerifiedHash is not null)
            {
                await using var destination = new FileStream(file.Destination, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await HashStreamAsync(destination, token).ConfigureAwait(false));
                if (hash != file.VerifiedHash) throw new IOException("A published destination failed SHA-256 verification. The source was retained.");
            }
            else if (File.Exists(file.Source))
            {
                ValidateSource(file);
                // Even unverified copies must prove a recovered publish belongs to this job.
                await using var source = File.OpenRead(file.Source);
                await using var destination = File.OpenRead(file.Destination);
                await ValidatePrefixAsync(source, destination, file.Length, 1024 * 1024, token).ConfigureAwait(false);
            }
            else throw new IOException("The unverified source is unavailable; the existing destination was retained for review.");
            if (state.Operation == TransferOperation.Move && File.Exists(file.Source))
                await DeleteSourceAfterVerificationAsync(file, token).ConfigureAwait(false);
        }
        lock (state.Sync) file.DestinationCommitted = true;
        if (!file.AtomicMove) ApplyMetadata(file);
        await MarkCompletedAsync(state, file).ConfigureAwait(false);
    }

    private static void ApplyMetadata(FilePlan file)
    {
        File.SetLastWriteTimeUtc(file.Destination, new DateTime(file.LastWriteTicks, DateTimeKind.Utc));
        if (file.CreationTicks > 0) File.SetCreationTimeUtc(file.Destination, new DateTime(file.CreationTicks, DateTimeKind.Utc));
        const FileAttributes preserved = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;
        var attributes = file.Attributes & preserved;
        File.SetAttributes(file.Destination, attributes == 0 ? FileAttributes.Normal : attributes);
    }

    private static async Task DeleteSourceAfterVerificationAsync(FilePlan file, CancellationToken token)
    {
        if (file.VerifiedHash is null) throw new IOException("A move cannot delete its source without a verified destination hash.");
        // Keep the destination read-locked until the verified source has been removed.
        await using var destination = new FileStream(file.Destination, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var destinationHash = Convert.ToHexString(await HashStreamAsync(destination, token).ConfigureAwait(false));
        if (destinationHash != file.VerifiedHash) throw new IOException("The destination changed before source removal; the source was retained.");
        await using var source = VolumeIdentity.OpenForVerifiedDelete(file.Source, 1024 * 1024);
        ValidateSource(file);
        VolumeIdentity.RejectNamedStreamsForMove(file.Source);
        var sourceHash = Convert.ToHexString(await HashStreamAsync(source, token).ConfigureAwait(false));
        if (sourceHash != file.VerifiedHash) throw new IOException("The source changed before removal; both files were retained.");
        ValidateSource(file);
        token.ThrowIfCancellationRequested();
        VolumeIdentity.RejectNamedStreamsForMove(file.Source);
        VolumeIdentity.DeleteVerifiedSource(source, file.Source);
    }

    private static async Task ValidatePrefixAsync(FileStream source, FileStream destination, long length, int bufferSize, CancellationToken token, IncrementalHash? sourceHasher = null)
    {
        var first = ArrayPool<byte>.Shared.Rent(bufferSize);
        var second = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            source.Position = destination.Position = 0;
            long offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(bufferSize, length - offset);
                await source.ReadExactlyAsync(first.AsMemory(0, count), token).ConfigureAwait(false);
                await destination.ReadExactlyAsync(second.AsMemory(0, count), token).ConfigureAwait(false);
                if (!first.AsSpan(0, count).SequenceEqual(second.AsSpan(0, count)))
                    throw new IOException("The partial file does not match the source. Both were retained; start a new transfer to recopy safely.");
                sourceHasher?.AppendData(first, 0, count);
                offset += count;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(first); ArrayPool<byte>.Shared.Return(second); }
    }

    private static async Task<byte[]> HashStreamAsync(FileStream stream, CancellationToken token)
    {
        // HashDataAsync uses small reads. With FileStream buffering disabled this becomes
        // one native asynchronous I/O operation per few KiB; use the copy engine's pooled blocks.
        var size = (int)Math.Clamp(stream.Length, 64 * 1024, 4 * 1024 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false)) != 0)
                hash.AppendData(buffer, 0, read);
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidateSource(FilePlan file)
    {
        RejectReparse(file.Source);
        var source = new FileInfo(file.Source);
        if (!source.Exists) throw new FileNotFoundException("The source file is unavailable.", file.Source);
        if (source.Length != file.Length || source.LastWriteTimeUtc.Ticks != file.LastWriteTicks)
            throw new IOException($"The source changed after this job was created; it was kept unchanged: {file.Source}");
    }

    private static void CleanupEmptySources(JobState state)
    {
        foreach (var directory in state.SourceDirectories.OrderByDescending(p => p.Length))
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    RejectReparse(directory);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        VolumeIdentity.RejectNamedStreamsForMove(directory);
                        Directory.Delete(directory, recursive: false);
                    }
                }
            }
            catch (IOException) { /* New/locked entries in the source are retained. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool SameVolume(JobState state, string source, string destination)
    {
        var sourceRoot = VolumeIdentity.Root(source);
        var destinationRoot = VolumeIdentity.Root(destination);
        return state.Volumes.First(v => v.Root.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)).Identity
            == state.Volumes.First(v => v.Root.Equals(destinationRoot, StringComparison.OrdinalIgnoreCase)).Identity;
    }

    private static void EnsureOriginalVolumesPresent(JobState state)
    {
        foreach (var volume in state.Volumes)
        {
            if (!string.Equals(VolumeIdentity.Get(volume.Root), volume.Identity, StringComparison.OrdinalIgnoreCase))
                throw new DriveUnavailableException("An original drive was disconnected or its drive letter was reassigned. Reconnect the original drive to resume safely.");
        }
    }

    private bool ResolveVolumes(JobState state)
    {
        lock (state.Sync)
        {
            var map = VolumeIdentity.Resolve(state.Volumes);
            if (map is null) return false;
            string Rebase(string path)
            {
                var root = VolumeIdentity.Root(path);
                return map.TryGetValue(root, out var current) && !current.Equals(root, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(current, path[root.Length..]) : path;
            }
            state.Sources = state.Sources.Select(Rebase).ToList();
            state.SourceSnapshot = state.Sources.AsReadOnly();
            state.DestinationDirectory = Rebase(state.DestinationDirectory);
            state.SourceDirectories = state.SourceDirectories.Select(Rebase).ToList();
            state.DestinationDirectories = state.DestinationDirectories.Select(Rebase).ToList();
            foreach (var file in state.Files)
            {
                file.Source = Rebase(file.Source); file.Destination = Rebase(file.Destination); file.Partial = Rebase(file.Partial);
            }
            foreach (var volume in state.Volumes) volume.Root = map[volume.Root];
            if (map.Any(pair => !pair.Key.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))) Save(state, rewriteManifest: true);
            return true;
        }
    }

    private void DurableCheckpoint(JobState state, FilePlan file, FileStream stream)
    {
        stream.Flush(flushToDisk: true); // Data reaches stable storage before the JSON can claim its offset.
        lock (state.Sync) file.DurableOffset = file.Offset;
        Save(state, file);
    }

    private Task MarkCompletedAsync(JobState state, FilePlan file)
    {
        SetOffset(state, file, file.Length);
        lock (state.Sync)
        {
            if (!file.Completed) state.CompletedFiles++;
            file.Completed = true;
            file.DurableOffset = file.Length;
        }
        // The data and ready-to-publish intent are already durable. A lost completion record
        // is recovered by validating that intent's final file, so it needs no extra disk barrier.
        // The next intent/status flush also makes earlier completion entries durable.
        Save(state, file, durable: false);
        Publish(state);
        return Task.CompletedTask;
    }

    private void SetStatus(JobState state, TransferStatus status, string? error = null)
    {
        lock (state.Sync) { state.Status = status; state.Error = error; state.UpdatedAt = DateTimeOffset.UtcNow; }
        Save(state);
        Publish(state);
    }

}
