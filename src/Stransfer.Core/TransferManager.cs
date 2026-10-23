using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stransfer.Core;

/// <summary>
/// Durable transfers with bounded buffers, conservative storage detection and grouped journal commits.
/// </summary>
public sealed partial class TransferManager : IDisposable
{
    private readonly string _stateDirectory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _planningGate = new(1);
    private readonly Dictionary<Guid, JobState> _jobs = [];
    private readonly List<string> _recoveryWarnings = [];
    internal Func<string, StorageSpace> ReadSpace { get; set; } = StorageProbe.Read;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public TransferManager(string? stateDirectory = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stransfer", "Jobs"));
        Directory.CreateDirectory(_stateDirectory);
        foreach (var path in Directory.EnumerateFiles(_stateDirectory, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<JobState>(File.ReadAllBytes(path), JsonOptions);
                if (state is null || state.Version != 1 || state.Id == Guid.Empty || Path.GetFileNameWithoutExtension(path) != state.Id.ToString("N"))
                    throw new InvalidDataException("Unsupported or invalid transfer record.");
                ValidateRecord(state);
                for (var index = 0; index < state.Files.Count; index++) state.Files[index].Index = index;
                ReplayJournal(state);
                state.CurrentFile = null;
                foreach (var file in state.Files) file.Offset = file.Completed ? file.Length : file.DurableOffset;
                RecalculateProgress(state);
                state.ManifestSaved = true;
                state.ManifestLength = new FileInfo(path).Length;
                if (state.Status is TransferStatus.Running or TransferStatus.Scanning or TransferStatus.Queued)
                {
                    state.Status = TransferStatus.Paused;
                    state.CurrentFile = null;
                }
                _jobs[state.Id] = state;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                _recoveryWarnings.Add($"Could not load {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    public event EventHandler<TransferJobSnapshot>? JobChanged;
    public IReadOnlyList<string> RecoveryWarnings => _recoveryWarnings.AsReadOnly();
    public IReadOnlyList<TransferJobSnapshot> Jobs
    {
        get
        {
            JobState[] states;
            lock (_sync) states = _jobs.Values.ToArray();
            return states.Select(Snapshot).OrderByDescending(j => j.CreatedAt).ToArray();
        }
    }

    /// <summary>Scans and durably accepts a job. Existing destinations are always kept with a numbered name.</summary>
    public async Task<TransferJobSnapshot> CreateJobAsync(IEnumerable<string> sources, string destination,
        TransferOperation operation = TransferOperation.Copy, bool verify = true, CancellationToken cancellationToken = default,
        TransferOptions? options = null)
    {
        await _planningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateJobCoreAsync(sources, destination, operation, verify, cancellationToken, options).ConfigureAwait(false); }
        finally { _planningGate.Release(); }
    }

    private async Task<TransferJobSnapshot> CreateJobCoreAsync(IEnumerable<string> sources, string destination,
        TransferOperation operation, bool verify, CancellationToken cancellationToken, TransferOptions? options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sources);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        options ??= new();
        if (options.SmallFileWorkers is not (0 or 1 or 2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(options));
        var paths = sources.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
        if (paths.Count == 0) throw new ArgumentException("Choose at least one file or folder.", nameof(sources));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (File.Exists(target)) throw new IOException("The destination must be a folder.");
        EnsureNoReparseAncestors(target);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("A selected source is unavailable.", path);
            EnsureNoReparseAncestors(path);
            if (Directory.Exists(path) && IsInside(target, path))
                throw new IOException("A folder cannot be transferred into itself or one of its subfolders.");
        }
        var selectedDirectories = paths.Where(Directory.Exists).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        paths = paths.Where(path =>
        {
            var ancestor = Path.GetDirectoryName(path);
            while (ancestor is not null)
            {
                if (selectedDirectories.Contains(ancestor)) return false;
                ancestor = Path.GetDirectoryName(ancestor);
            }
            return true;
        }).ToList();
        var state = new JobState
        {
            Id = Guid.NewGuid(), Operation = operation, Verify = verify, Options = options,
            DestinationDirectory = target, Sources = paths, Status = TransferStatus.Scanning,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await Task.Run(() => Scan(state, cancellationToken), cancellationToken).ConfigureAwait(false);
        RecalculateProgress(state);
        state.Preflight = await Task.Run(() => Inspect(state, cancellationToken, includeQueuedJobs: true), cancellationToken).ConfigureAwait(false);
        if (!state.Preflight.CanStart) throw new TransferPreflightException(state.Preflight);
        cancellationToken.ThrowIfCancellationRequested();
        state.Status = TransferStatus.Queued;
        Save(state);
        lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); _jobs.Add(state.Id, state); }
        Publish(state);
        return Snapshot(state);
    }

    /// <summary>Runs or resumes a job. Transfer errors are exposed in its snapshot; cancellation durably pauses it.</summary>
    public async Task RunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Get(id);
        CancellationTokenSource linked;
        lock (state.Sync)
        {
            if (state.Status == TransferStatus.Completed || state.IsExecuting) return;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            state.Cancellation = linked;
            state.IsExecuting = true;
            state.Error = null;
        }
        var token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!ResolveVolumes(state)) { SetStatus(state, TransferStatus.WaitingForDrive, "Reconnect the original source and destination drives."); return; }
            state.Preflight = await Task.Run(() => Inspect(state, token), token).ConfigureAwait(false);
            if (!state.Preflight.CanStart) throw new TransferPreflightException(state.Preflight);
            foreach (var file in state.Files.Where(file => !file.Completed)) SetOffset(state, file, file.DurableOffset);
            SetStatus(state, TransferStatus.Running);
            state.DirectoryGuard = new();
            foreach (var directory in state.DestinationDirectories)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoReparseAncestors(directory);
                Directory.CreateDirectory(directory);
                state.DirectoryGuard.Pin(directory);
            }
            var workers = WorkerCount(state);
            state.EffectiveWorkers = workers;
            var smallFiles = state.Files.Where(file => !file.Completed && file.Length <= 1024 * 1024).ToArray();
            if (workers > 1 && smallFiles.Length > 1)
                await Parallel.ForEachAsync(smallFiles, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token },
                    async (file, workerToken) =>
                    {
                        lock (state.Sync) state.CurrentFile = file.Source;
                        await TransferWithRetryAsync(state, file, workerToken).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            foreach (var file in state.Files)
            {
                token.ThrowIfCancellationRequested();
                if (file.Completed) continue;
                lock (state.Sync) state.CurrentFile = file.Source;
                Publish(state);
                token.ThrowIfCancellationRequested();
                await TransferWithRetryAsync(state, file, token).ConfigureAwait(false);
            }
            state.DirectoryGuard.Dispose(); state.DirectoryGuard = null;
            if (state.Operation == TransferOperation.Move) CleanupEmptySources(state);
            lock (state.Sync) state.CurrentFile = null;
            SetStatus(state, TransferStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            lock (state.Sync) state.CurrentFile = null;
            SetStatus(state, TransferStatus.Paused);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or NotSupportedException or ArgumentException)
        {
            lock (state.Sync) state.CurrentFile = null;
            var available = AreDrivesAvailable(id);
            SetStatus(state, ex is DriveUnavailableException || !available ? TransferStatus.WaitingForDrive : TransferStatus.Failed, ex.Message);
        }
        finally
        {
            state.DirectoryGuard?.Dispose(); state.DirectoryGuard = null;
            lock (state.Sync) { state.IsExecuting = false; state.Cancellation = null; }
            linked.Dispose();
        }
    }

    public void Pause(Guid id)
    {
        var state = Get(id);
        lock (state.Sync) state.Cancellation?.Cancel();
    }

    /// <summary>Returns whether the original volumes are present, even if their drive letters have changed.</summary>
    public bool AreDrivesAvailable(Guid id)
    {
        var state = Get(id);
        lock (state.Sync)
        {
            try { return VolumeIdentity.Resolve(state.Volumes) is not null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Removes a stopped queue record. Published destinations and sources are never removed.</summary>
    public Task RemoveAsync(Guid id, bool deletePartialFiles = false, CancellationToken cancellationToken = default)
    {
        var state = Get(id);
        lock (state.Sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.IsExecuting) throw new InvalidOperationException("Pause the transfer and wait for it to stop before removing it.");
            if (deletePartialFiles)
            {
                if (!ResolveVolumes(state)) throw new IOException("Reconnect the original drives before deleting partial files.");
                foreach (var file in state.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureNoReparseAncestors(file.Partial);
                    if (File.Exists(file.Partial)) File.Delete(file.Partial);
                }
            }
            File.Delete(StatePath(state));
            File.Delete(JournalPath(state));
            lock (_sync) _jobs.Remove(id);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        JobState[] states;
        lock (_sync) { if (_disposed) return; _disposed = true; states = _jobs.Values.ToArray(); }
        foreach (var state in states) lock (state.Sync) state.Cancellation?.Cancel();
    }

    private JobState Get(Guid id)
    {
        lock (_sync) return _jobs.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException("The transfer job was not found.");
    }

    private static TransferJobSnapshot Snapshot(JobState state)
    {
        lock (state.Sync) return new TransferJobSnapshot
        {
            Id = state.Id, Operation = state.Operation, Status = state.Status, DestinationDirectory = state.DestinationDirectory,
            Sources = state.SourceSnapshot, TotalBytes = state.TotalBytes, BytesTransferred = state.BytesTransferred,
            TotalFiles = state.Files.Count, CompletedFiles = state.CompletedFiles, CurrentFile = state.CurrentFile,
            Error = state.Error, CreatedAt = state.CreatedAt, UpdatedAt = state.UpdatedAt, Verify = state.Verify,
            Preflight = state.Preflight, SmallFileWorkers = state.Options.SmallFileWorkers
        };
    }

    private void Publish(JobState state)
    {
        var snapshot = Snapshot(state);
        if (JobChanged is not { } handlers) return;
        foreach (EventHandler<TransferJobSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch { /* UI subscriber failures cannot interrupt a durable file operation. */ }
        }
    }

    private static string UniqueDestination(string requested, bool directory, HashSet<string> reserved, Dictionary<string, int> nextSuffix)
    {
        var candidate = requested;
        var extension = directory ? "" : Path.GetExtension(requested);
        var parent = Path.GetDirectoryName(requested)!;
        var name = Path.GetFileName(requested);
        var stem = directory ? name : name[..^extension.Length];
        var suffix = nextSuffix.GetValueOrDefault(requested, 2);
        if (nextSuffix.ContainsKey(requested)) candidate = NumberedName(suffix++);
        while (File.Exists(candidate) || Directory.Exists(candidate) || !reserved.Add(candidate)) candidate = NumberedName(suffix++);
        nextSuffix[requested] = suffix;
        return candidate;

        string NumberedName(int numberValue)
        {
            var number = $" ({numberValue})";
            var keptExtension = extension[..Math.Min(extension.Length, 255 - number.Length - 1)];
            var keptStem = stem[..Math.Min(stem.Length, 255 - number.Length - keptExtension.Length)];
            return Path.Combine(parent, keptStem + number + keptExtension);
        }
    }

    private static bool IsInside(string path, string parent) => path.Equals(parent, PathComparison)
        || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);

    private static void SetOffset(JobState state, FilePlan file, long offset)
    {
        lock (state.Sync) { state.BytesTransferred += offset - file.Offset; file.Offset = offset; }
    }

    private static void RecalculateProgress(JobState state)
    {
        state.SourceSnapshot = state.Sources.AsReadOnly();
        state.TotalBytes = state.Files.Sum(file => file.Length);
        state.BytesTransferred = state.Files.Sum(file => file.Offset);
        state.CompletedFiles = state.Files.Count(file => file.Completed);
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Links and junctions are not followed: {path}");
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current)) RejectReparse(current);
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }

    private static void ValidateRecord(JobState state)
    {
        if (state.Sources is null || state.Files is null || state.Volumes is null || state.SourceDirectories is null || state.DestinationDirectories is null
            || state.Options is null || state.Options.SmallFileWorkers is not (0 or 1 or 2 or 4 or 8)
            || !Enum.IsDefined(state.Status) || !Enum.IsDefined(state.Operation) || !Path.IsPathFullyQualified(state.DestinationDirectory)
            || state.Sources.Count == 0 || state.Volumes.Count == 0)
            throw new InvalidDataException("Invalid transfer state.");
        var sourceSet = state.Sources.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool IsWithinSources(string path)
        {
            string? current = path;
            while (current is not null)
            {
                if (sourceSet.Contains(current)) return true;
                current = Path.GetDirectoryName(current);
            }
            return false;
        }
        if (state.Sources.Any(source => !Path.IsPathFullyQualified(source))
            || state.Volumes.Any(volume => volume is null || !Path.IsPathFullyQualified(volume.Root) || string.IsNullOrWhiteSpace(volume.Identity))
            || state.DestinationDirectories.Any(directory => !Path.IsPathFullyQualified(directory) || !IsInside(directory, state.DestinationDirectory))
            || state.SourceDirectories.Any(directory => !Path.IsPathFullyQualified(directory) || !IsWithinSources(directory)))
            throw new InvalidDataException("Invalid directory or volume in transfer state.");
        bool Canonical(string path) => Path.GetFullPath(path).Equals(path, PathComparison);
        if (!Canonical(state.DestinationDirectory) || state.Sources.Any(source => !Canonical(source))
            || state.SourceDirectories.Any(directory => !Canonical(directory)) || state.DestinationDirectories.Any(directory => !Canonical(directory)))
            throw new InvalidDataException("Transfer paths must be canonical.");
        foreach (var file in state.Files)
        {
            if (file is null || !Path.IsPathFullyQualified(file.Source) || !Path.IsPathFullyQualified(file.Destination) || !Path.IsPathFullyQualified(file.Partial)
                || !Canonical(file.Source) || !Canonical(file.Destination) || !Canonical(file.Partial)
                || file.Length < 0 || file.Offset < 0 || file.Offset > file.Length || file.DurableOffset < 0 || file.DurableOffset > file.Length
                || !IsInside(file.Destination, state.DestinationDirectory)
                || Path.GetDirectoryName(file.Partial) != Path.GetDirectoryName(file.Destination)
                || !Path.GetFileName(file.Partial).Contains($".stransfer-{state.Id:N}-", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid file entry in transfer state.");
        }
    }
}

internal sealed class DriveUnavailableException(string message) : IOException(message);
