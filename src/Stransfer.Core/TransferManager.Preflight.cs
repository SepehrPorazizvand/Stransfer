namespace Stransfer.Core;

public sealed partial class TransferManager
{
    /// <summary>Rechecks remaining allocation, filesystem limits, sources and write access without starting a job.</summary>
    public Task<TransferPreflight> CheckAsync(Guid id, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var state = Get(id);
        lock (state.Sync)
        {
            if (state.IsExecuting) throw new InvalidOperationException("Pause this transfer before checking it.");
            if (!ResolveVolumes(state)) throw new IOException("Reconnect the original drives.");
            return Inspect(state, cancellationToken);
        }
    }, cancellationToken);

    private TransferPreflight Inspect(JobState state, CancellationToken token, bool includeQueuedJobs = false)
    {
        var issues = new List<string>();
        var ancestor = state.DestinationDirectory;
        while (!Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("The destination drive is unavailable.");
        EnsureNoReparseAncestors(ancestor);
        var space = ReadSpace(ancestor);
        long required = 0;
        long Round(long count) => checked((count + space.ClusterBytes - 1) / space.ClusterBytes * space.ClusterBytes);
        foreach (var file in state.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Completed || file.ReadyToCommit && File.Exists(file.Destination)) continue;
            ValidateSource(file);
            if (state.Operation == TransferOperation.Move && SameVolume(state, file.Source, file.Destination)) continue;
            // Reject data the streaming engine cannot faithfully represent before making changes.
            VolumeIdentity.RejectNamedStreamsForMove(file.Source);
            if ((file.Attributes & FileAttributes.Encrypted) != 0)
                issues.Add($"Encrypted files require a native EFS-aware transfer: {file.Source}");
            if (space.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && file.Length > uint.MaxValue)
                issues.Add($"FAT32 cannot hold a file larger than 4 GiB minus 1 byte: {file.Source}");
            long existing = File.Exists(file.Partial) ? new FileInfo(file.Partial).Length : 0;
            required = checked(required + Math.Max(0, Round(file.Length) - Round(Math.Min(existing, file.Length))));
        }
        // Conservative metadata estimate plus headroom. Other programs can still consume this space later.
        required = checked(required + state.DestinationDirectories.Count(directory => !Directory.Exists(directory)) * Math.Max(space.ClusterBytes, 4096));
        var margin = required == 0 ? 0 : Math.Max(4L * 1024 * 1024, Math.Min(256L * 1024 * 1024, required / 100));
        long reserved = includeQueuedJobs ? QueuedAllocation(state, space.ClusterBytes) : 0;
        if (space.AvailableBytes < required + margin + reserved)
            issues.Add($"Not enough destination space. Need {required + margin + reserved:N0} bytes including headroom and queued transfers; {space.AvailableBytes:N0} bytes are available.");
        var probe = Path.Combine(ancestor, $".stransfer-access-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { issues.Add("Destination is not writable: " + error.Message); }
        return new() { RequiredBytes = required, AvailableBytes = space.AvailableBytes, SafetyMarginBytes = margin,
            ReservedByQueuedJobsBytes = reserved, FileCount = state.Files.Count(file => !file.Completed), FileSystem = space.FileSystem, Issues = issues };
    }

    private long QueuedAllocation(JobState state, long cluster)
    {
        JobState[] others;
        lock (_sync) others = _jobs.Values.Where(other => other.Id != state.Id).ToArray();
        var identity = state.Volumes.First(volume => volume.Root.Equals(VolumeIdentity.Root(state.DestinationDirectory), StringComparison.OrdinalIgnoreCase)).Identity;
        long reserved = 0;
        foreach (var other in others)
        {
            lock (other.Sync)
            {
                if (other.Status is TransferStatus.Completed or TransferStatus.Canceled) continue;
                if (!other.Volumes.Any(volume => volume.Identity == identity && volume.Root.Equals(VolumeIdentity.Root(other.DestinationDirectory), StringComparison.OrdinalIgnoreCase))) continue;
                long remaining = 0;
                foreach (var file in other.Files)
                {
                    if (file.Completed || file.ReadyToCommit && File.Exists(file.Destination)
                        || other.Operation == TransferOperation.Move && SameVolume(other, file.Source, file.Destination)) continue;
                    long existing = 0;
                    try { if (File.Exists(file.Partial)) existing = new FileInfo(file.Partial).Length; } catch (FileNotFoundException) { }
                    remaining = checked(remaining + Math.Max(0, Round(file.Length) - Round(Math.Min(existing, file.Length))));
                }
                remaining = checked(remaining + other.DestinationDirectories.Count(directory => !Directory.Exists(directory)) * Math.Max(cluster, 4096));
                if (remaining > 0) reserved = checked(reserved + remaining + Math.Max(4L * 1024 * 1024, Math.Min(256L * 1024 * 1024, remaining / 100)));
            }
        }
        return reserved;
        long Round(long bytes) => checked((bytes + cluster - 1) / cluster * cluster);
    }

    private static int WorkerCount(JobState state)
    {
        if (state.Options.SmallFileWorkers != 0) return state.Options.SmallFileWorkers;
        return StorageProbe.IsNonSeeking(state.DestinationDirectory)
            && state.Sources.All(StorageProbe.IsNonSeeking) ? 4 : 1;
    }
}
