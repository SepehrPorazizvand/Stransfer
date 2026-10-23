using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stransfer.Core;

public sealed partial class TransferManager
{
    private void Scan(JobState state, CancellationToken token)
    {
        var roots = state.Sources.Append(state.DestinationDirectory).Select(VolumeIdentity.Root).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var identity = VolumeIdentity.Get(root) ?? throw new IOException($"The drive {root} is unavailable.");
            state.Volumes.Add(new VolumeBinding { Root = root, Identity = identity });
        }
        state.DestinationDirectories.Add(state.DestinationDirectory);
        JobState[] otherJobs;
        lock (_sync) otherJobs = _jobs.Values.ToArray();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in otherJobs)
            lock (other.Sync)
            {
                if (other.Status is TransferStatus.Completed or TransferStatus.Canceled) continue;
                foreach (var file in other.Files) reserved.Add(file.Destination);
                foreach (var directory in other.DestinationDirectories)
                    if (!directory.Equals(other.DestinationDirectory, PathComparison)) reserved.Add(directory);
            }
        var nextSuffix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in state.Sources)
        {
            token.ThrowIfCancellationRequested();
            var directory = Directory.Exists(source);
            var name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name)) name = "Drive-" + VolumeIdentity.Root(source).TrimEnd('\\', '/').Replace(':', '-');
            var destination = UniqueDestination(Path.Combine(state.DestinationDirectory, name), directory, reserved, nextSuffix);
            if (directory) ScanDirectory(state, source, destination, token);
            else AddFile(state, source, destination);
        }
    }

    private static void ScanDirectory(JobState state, string source, string destination, CancellationToken token)
    {
        // An explicit stack avoids call-stack overflow on deeply nested folder trees.
        var stack = new Stack<(string Source, string Destination)>();
        stack.Push((source, destination));
        while (stack.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            RejectReparse(current.Source);
            VolumeIdentity.RejectNamedStreamsForMove(current.Source);
            state.SourceDirectories.Add(current.Source);
            state.DestinationDirectories.Add(current.Destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Source))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Links and junctions are not followed: {entry}");
                var target = Path.Combine(current.Destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) stack.Push((entry, target));
                else AddFile(state, entry, target);
            }
        }
    }

    private static void AddFile(JobState state, string source, string destination)
    {
        RejectReparse(source);
        var info = new FileInfo(source);
        state.Files.Add(new FilePlan
        {
            Source = source, Destination = destination,
            Index = state.Files.Count,
            Partial = Path.Combine(Path.GetDirectoryName(destination)!, $".stransfer-{state.Id:N}-{state.Files.Count}.partial"),
            Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, CreationTicks = info.CreationTimeUtc.Ticks, Attributes = info.Attributes
        });
    }

}
