using System.Security.Cryptography;
using System.Buffers;
using Stransfer.Core;

namespace Stransfer.App.Services;

internal sealed partial class NamespaceTransferManager
{
    private void Copy(NamespaceJob job, NamespaceFile file, int index, string parent, TransferStorage storage, byte[] buffer, byte[] compare, CancellationToken token)
    {
        var finalName = Leaf(file.Relative);
        var partialName = $".stransfer-{job.Id:N}-{index}.partial";
        var final = storage.Find(parent, finalName);
        if (file.Ready && final is not null)
        {
            if (final.Directory || final.Length != file.Length || Hash(storage.Read(final.Location), buffer, token) != file.Hash)
                throw new IOException("The destination changed after the saved commit: " + file.Relative);
            file.Committed = true; file.Offset = file.Length; Append(job, index);
            storage.ApplyModifiedTime(final.Location, file.Modified, TransferStorage.IsShell(file.Source));
            FinishMove(job, file, final.Location, storage, buffer, token);
            file.Completed = true; Append(job, index); Publish(job); return;
        }
        if (file.Committed) throw new IOException("A committed destination is missing. The source was retained: " + file.Relative);
        if (final is not null) throw new IOException("A new file occupies the planned destination. It was kept: " + file.Relative);
        var source = storage.Stat(file.Source);
        if (source.Length != file.Length || source.Modified != file.Modified || source.Directory) throw new IOException("Source changed: " + file.Relative);
        var partial = storage.Find(parent, partialName);
        if (partial is not null && (partial.Directory || file.Partial is not null && file.Partial != partial.Location))
            throw new IOException("The partial file was replaced. It was kept for inspection.");
        if (partial is not null && file.Partial is null)
        {
            // Recovery after create but before the first checkpoint: no source deletion or overwrite.
            file.Partial = partial.Location; file.Offset = 0; Append(job, index);
        }
        if (partial is null) { file.Offset = 0; file.Partial = null; file.Ready = false; file.Hash = null; }
        else if (TransferStorage.IsShell(parent))
        {
            // MTP commonly exposes sequential writes only. Never assume Seek or overwrite support.
            storage.Delete(partial.Location); partial = null; file.Partial = null; file.Offset = 0; file.Ready = false; file.Hash = null; Append(job, index);
        }
        using var input = storage.Read(file.Source);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream? output = null;
        try
        {
            if (partial is null)
            {
                // The unique name is deterministic, so an interruption between create and journal is recoverable.
                (var location, output) = storage.Create(parent, partialName, file.Length);
                file.Partial = location; file.Offset = 0; Append(job, index);
            }
            else
            {
                output = storage.ReopenWrite(partial.Location);
                if (output.Length < file.Offset) { file.Offset = 0; Append(job, index); }
                output.Position = 0;
                long left = file.Offset;
                while (left > 0)
                {
                    token.ThrowIfCancellationRequested(); int wanted = (int)Math.Min(buffer.Length, left);
                    ReadExactly(input, buffer, wanted, token); ReadExactly(output, compare, wanted, token);
                    if (!buffer.AsSpan(0, wanted).SequenceEqual(compare.AsSpan(0, wanted)))
                        throw new IOException("The saved prefix no longer matches its source. Partial data was retained: " + file.Relative);
                    digest.AppendData(buffer, 0, wanted); left -= wanted;
                }
                output.SetLength(file.Offset); output.Position = file.Offset;
            }
            var checkpoint = file.Offset; var lastSave = Stopwatch.StartNew(); var lastPublish = Stopwatch.StartNew();
            long offset = file.Offset;
            while (offset < file.Length)
            {
                token.ThrowIfCancellationRequested(); int wanted = (int)Math.Min(buffer.Length, file.Length - offset);
                int read = input.Read(buffer, 0, wanted); if (read == 0) throw new EndOfStreamException("The source ended before its scanned size.");
                output.Write(buffer, 0, read); digest.AppendData(buffer, 0, read); offset += read;
                // Display live progress; only flushed offsets enter the durable journal.
                lock (job.Sync) file.Offset = offset;
                if (lastPublish.ElapsedMilliseconds >= 150) { Publish(job); lastPublish.Restart(); }
                if (!TransferStorage.IsShell(parent) && (offset - checkpoint >= 32L * 1024 * 1024 || lastSave.ElapsedMilliseconds >= 1000))
                { Flush(output); Append(job, index); checkpoint = offset; lastSave.Restart(); Publish(job, force: true); }
            }
            token.ThrowIfCancellationRequested();
            if (input.Read(buffer, 0, 1) != 0) throw new IOException("The source grew during transfer.");
            var after = storage.Stat(file.Source);
            if (after.Length != file.Length || after.Modified != file.Modified) throw new IOException("Source changed during transfer.");
            Flush(output); output.Dispose(); output = null;
            if (TransferStorage.IsShell(parent))
            {
                // WPD may allocate a new object identifier only when its stream is committed.
                file.Partial = storage.RefreshWritten(parent, partialName, file.Partial!); Append(job, index);
            }
            var hash = Convert.ToHexString(digest.GetHashAndReset());
            // Device destinations always read back before commit; moves always verify before deletion.
            if (job.Verify || job.Operation == TransferOperation.Move || TransferStorage.IsShell(parent))
                if (Hash(storage.Read(file.Partial!), buffer, token) != hash) throw new IOException("Destination verification failed. The source was retained.");
            file.Hash = hash; file.Ready = true; file.Offset = file.Length; Append(job, index);
            token.ThrowIfCancellationRequested();
            if (storage.Find(parent, finalName) is not null) throw new IOException("A destination appeared during transfer. It was kept.");
            var committed = storage.Rename(file.Partial!, parent, finalName);
            file.Committed = true; Append(job, index);
            storage.ApplyModifiedTime(committed, file.Modified, TransferStorage.IsShell(file.Source));
            input.Dispose();
            FinishMove(job, file, committed, storage, buffer, token);
            file.Completed = true; Append(job, index); Publish(job);
        }
        catch
        {
            if (output is not null && !TransferStorage.IsShell(parent))
            {
                try { Flush(output); Append(job, index); }
                catch { /* Keep the earlier durable checkpoint if the drive is gone. */ }
            }
            else if (!file.Ready) { file.Offset = 0; Append(job, index); }
            throw;
        }
        finally { output?.Dispose(); }
    }
    private static void FinishMove(NamespaceJob job, NamespaceFile file, string destination, TransferStorage storage, byte[] buffer, CancellationToken token)
    {
        if (job.Operation != TransferOperation.Move) return;
        token.ThrowIfCancellationRequested();
        if (Hash(storage.Read(destination), buffer, token) != file.Hash) throw new IOException("Destination changed before source deletion. Source retained.");
        if (!TransferStorage.IsShell(file.Source))
        {
            // Delete the same verified Windows file by handle, preventing replacement/write races.
            if (!File.Exists(file.Source)) return; // Recovery after a successful delete whose journal was interrupted.
            VolumeIdentity.RejectNamedStreamsForMove(file.Source);
            using var original = VolumeIdentity.OpenForVerifiedDelete(file.Source, 1);
            if (original.Length != file.Length || Hash(original, buffer, token, dispose: false) != file.Hash) throw new IOException("Source changed before deletion. It was retained.");
            token.ThrowIfCancellationRequested(); VolumeIdentity.DeleteVerifiedSource(original, file.Source);
        }
        else
        {
            try
            {
                var source = storage.Stat(file.Source);
                if (source.Length != file.Length || source.Modified != file.Modified || Hash(storage.Read(file.Source), buffer, token) != file.Hash)
                    throw new IOException("Source changed before deletion. It was retained.");
                token.ThrowIfCancellationRequested(); storage.Delete(file.Source);
            }
            catch (FileNotFoundException) { }
        }
    }
    private static string Hash(Stream input, byte[] buffer, CancellationToken token, bool dispose = true)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read; while ((read = input.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { if (dispose) input.Dispose(); }
    }
    private static void ReadExactly(Stream input, byte[] buffer, int wanted, CancellationToken token)
    {
        int offset = 0;
        // COM IStream wrappers accept offset zero. Short reads are shifted with one bounded scratch buffer.
        while (offset < wanted)
        {
            token.ThrowIfCancellationRequested();
            if (offset == 0)
            { var read = input.Read(buffer, 0, wanted); if (read == 0) throw new EndOfStreamException(); offset = read; }
            else
            {
                var scratch = ArrayPool<byte>.Shared.Rent(wanted - offset);
                try { int read = input.Read(scratch, 0, wanted - offset); if (read == 0) throw new EndOfStreamException(); scratch.AsSpan(0, read).CopyTo(buffer.AsSpan(offset)); offset += read; }
                finally { ArrayPool<byte>.Shared.Return(scratch); }
            }
        }
    }
    private static void Flush(Stream output) { if (output is FileStream file) file.Flush(true); else output.Flush(); }
}
