using System.Text;
using System.Text.Json;
using Stransfer.Core;

namespace Stransfer.App.Services;

internal sealed partial class NamespaceTransferManager
{
    private string Manifest(NamespaceJob job) => Path.Combine(directory, job.Id.ToString("N") + ".json");
    private string Journal(NamespaceJob job) => Path.Combine(directory, job.Id.ToString("N") + ".journal");
    private void SaveManifest(NamespaceJob job)
    {
        var path = Manifest(job); var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, job); stream.Flush(true); }
        File.Move(temporary, path, overwrite: true);
    }
    private void Append(NamespaceJob job, int? index)
    {
        job.Updated = DateTimeOffset.UtcNow;
        var entry = new NamespaceProgress { Epoch = job.Epoch, Status = job.Status, Error = job.Error, Updated = job.Updated, Index = index,
            File = index is { } number ? job.Files[number] : null };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        using var stream = new FileStream(Journal(job), FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(true);
        // Keep the immutable scan out of the per-block/per-file hot path.
        if (stream.Length > Math.Max(4L * 1024 * 1024, job.Files.Count * 512L))
        {
            stream.Dispose(); job.Epoch = Guid.NewGuid(); SaveManifest(job);
            using var reset = new FileStream(Journal(job), FileMode.Create, FileAccess.Write, FileShare.Read); reset.Flush(true);
        }
    }
    private void Replay(NamespaceJob job)
    {
        var path = Journal(job); if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path); int start = 0, valid = 0;
        for (int end = 0; end < bytes.Length; end++)
        {
            if (bytes[end] != (byte)'\n') continue;
            var entry = JsonSerializer.Deserialize<NamespaceProgress>(bytes.AsSpan(start, end - start)) ?? throw new InvalidDataException("Invalid device journal entry.");
            if (entry.Epoch != job.Epoch) { start = end + 1; valid = start; continue; }
            if (!Enum.IsDefined(entry.Status)) throw new InvalidDataException("Invalid transfer status.");
            job.Status = entry.Status; job.Error = entry.Error; job.Updated = entry.Updated;
            if (entry.Index is { } index)
            {
                if (index < 0 || index >= job.Files.Count || entry.File is not { } value) throw new InvalidDataException("Invalid file checkpoint.");
                var original = job.Files[index];
                if (value.Source != original.Source || value.Relative != original.Relative || value.Length != original.Length || value.Modified != original.Modified)
                    throw new InvalidDataException("Checkpoint does not match its scanned file.");
                ValidateFile(job, value); job.Files[index] = value;
            }
            start = end + 1; valid = start;
        }
        // A power failure may leave a partial last line. Trim it before the next append.
        if (valid != bytes.Length)
        { using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); stream.SetLength(valid); stream.Flush(true); }
    }
    private static void Validate(NamespaceJob job)
    {
        if (job.Id == Guid.Empty || !Enum.IsDefined(job.Operation) || !Enum.IsDefined(job.Status) || job.Sources is null || job.Sources.Count == 0
            || job.Files is null || job.Directories is null || job.Volumes is null || job.SourceLabels is null) throw new InvalidDataException("Invalid device transfer plan.");
        ValidLocation(job.Destination);
        foreach (var source in job.Sources) ValidLocation(source);
        var relative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var folder in job.Directories)
        {
            ValidRelative(folder.Relative); ValidLocation(folder.Source);
            if (!job.Sources.Any(root => Within(folder.Source, root)) || !folders.Contains(Parent(folder.Relative)) || !relative.Add(folder.Relative))
                throw new InvalidDataException("Unsafe folder in transfer plan.");
            folders.Add(folder.Relative);
        }
        foreach (var file in job.Files)
        {
            ValidateFile(job, file);
            if (!folders.Contains(Parent(file.Relative)) || !relative.Add(file.Relative)) throw new InvalidDataException("Duplicate or unsafe target.");
        }
        foreach (var volume in job.Volumes)
            if (!Path.IsPathFullyQualified(volume.Root) || string.IsNullOrWhiteSpace(volume.Identity)) throw new InvalidDataException("Invalid volume identity.");
    }
    private static void ValidateFile(NamespaceJob job, NamespaceFile file)
    {
        ValidLocation(file.Source); ValidRelative(file.Relative);
        if (!job.Sources.Any(root => Within(file.Source, root)) || file.Length < 0 || file.Offset < 0 || file.Offset > file.Length
            || (file.Ready || file.Committed || file.Completed) && (file.Hash is null || file.Hash.Length != 64 || !file.Hash.All(Uri.IsHexDigit))
            || file.Committed && !file.Ready || file.Completed && !file.Committed)
            throw new InvalidDataException("Unsafe file checkpoint.");
        if (file.Partial is { } partial) { ValidLocation(partial); if (!Within(partial, job.Destination)) throw new InvalidDataException("Partial file escaped destination."); }
    }
    private static void ValidLocation(string path)
    {
        var value = TransferStorage.Display(path);
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !(Path.IsPathFullyQualified(value) || TransferStorage.IsShell(path) && value.StartsWith("::{", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid transfer endpoint.");
    }
    private static void ValidRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("Empty target name.");
        foreach (var name in relative.Split('\\')) TransferStorage.SafeName(name);
    }
    private bool Resolve(NamespaceJob job)
    {
        lock (job.Sync)
        {
            var mapping = VolumeIdentity.Resolve(job.Volumes); if (mapping is null) return false;
            if (mapping.All(pair => pair.Key.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))) return true;
            string Rebase(string path)
            {
                if (TransferStorage.IsShell(path)) return path;
                var root = VolumeIdentity.Root(path);
                return mapping.TryGetValue(root, out var replacement) ? replacement + path[root.Length..] : path;
            }
            job.Destination = Rebase(job.Destination);
            if (!TransferStorage.IsShell(job.Destination)) job.DestinationLabel = job.Destination;
            job.Sources = job.Sources.Select(Rebase).ToList();
            foreach (var folder in job.Directories) folder.Source = Rebase(folder.Source);
            foreach (var file in job.Files) { file.Source = Rebase(file.Source); if (file.Partial is { } partial) file.Partial = Rebase(partial); }
            foreach (var volume in job.Volumes) volume.Root = mapping[volume.Root];
            // Compact before future journal entries refer to the new volume paths.
            job.Epoch = Guid.NewGuid(); SaveManifest(job); using var reset = new FileStream(Journal(job), FileMode.Create, FileAccess.Write, FileShare.Read); reset.Flush(true);
            return true;
        }
    }
}

internal sealed class NamespaceProgress
{
    public Guid Epoch { get; set; }
    public TransferStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset Updated { get; set; }
    public int? Index { get; set; }
    public NamespaceFile? File { get; set; }
}
