using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stransfer.Core;

namespace Stransfer.App.Services;

/// <summary>Owns device transfer planning, streaming, verification and restart recovery.</summary>
internal sealed partial class NamespaceTransferManager : IDisposable
{
    private readonly string directory;
    private readonly object sync = new();
    private readonly Dictionary<Guid, NamespaceJob> jobs = [];
    private readonly SemaphoreSlim planning = new(1);
    internal Func<TransferStorage> StorageFactory { get; set; } = () => new();
    internal Exception? LastFailure { get; private set; }
    internal Func<Guid, string, long> OtherReservations { get; set; } = (_, _) => 0;
    public event EventHandler<TransferJobSnapshot>? JobChanged;
    public List<string> RecoveryWarnings { get; } = [];
    public IReadOnlyList<TransferJobSnapshot> Jobs { get { lock (sync) return jobs.Values.Select(Snapshot).ToArray(); } }
    public NamespaceTransferManager(string directory)
    {
        this.directory = Path.GetFullPath(directory); Directory.CreateDirectory(this.directory);
        foreach (var path in Directory.EnumerateFiles(this.directory, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<NamespaceJob>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty record.");
                if (job.Version != 1 || job.Id.ToString("N") != Path.GetFileNameWithoutExtension(path)) throw new InvalidDataException("Invalid device record.");
                Validate(job); Replay(job);
                if (job.Status is TransferStatus.Running or TransferStatus.Queued or TransferStatus.Scanning) job.Status = TransferStatus.Paused;
                job.Current = null; jobs.Add(job.Id, job);
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException or OverflowException)
            { RecoveryWarnings.Add("Could not recover device job " + Path.GetFileName(path) + ": " + error.Message); }
        }
    }
    public bool Contains(Guid id) { lock (sync) return jobs.ContainsKey(id); }
    public async Task<TransferJobSnapshot> CreateAsync(string[] sources, string destination, TransferOperation operation, bool verify, CancellationToken token = default)
    {
        await planning.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await NamespaceCom.Sta(() =>
            {
                token.ThrowIfCancellationRequested();
                if (sources.Length == 0 || !Enum.IsDefined(operation)) throw new ArgumentException("Choose files or folders.");
                using var storage = StorageFactory();
                destination = TransferStorage.IsShell(destination) ? destination : Path.GetFullPath(destination);
                sources = sources.Select(source => TransferStorage.IsShell(source) ? source : Path.GetFullPath(source)).ToArray();
                ValidLocation(destination); foreach (var source in sources) ValidLocation(source);
                var job = new NamespaceJob { Id = Guid.NewGuid(), Destination = destination, Sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Operation = operation, Verify = verify, Created = DateTimeOffset.UtcNow, Updated = DateTimeOffset.UtcNow };
                if (!storage.Stat(destination).Directory) throw new IOException("The destination must be a folder.");
                job.DestinationLabel = TransferStorage.IsShell(destination) ? storage.Stat(destination).Name : destination;
                var used = storage.Names(destination).Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                lock (sync)
                    foreach (var other in jobs.Values.Where(other => other.Destination == destination && other.Status != TransferStatus.Completed))
                        foreach (var name in other.Files.Select(file => file.Relative.Split('\\')[0]).Concat(other.Directories.Select(folder => folder.Relative.Split('\\')[0]))) used.Add(name);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var source in job.Sources)
                {
                    token.ThrowIfCancellationRequested(); var entry = storage.Stat(source);
                    if (Within(destination, source)) throw new IOException("A folder cannot be transferred into itself.");
                    job.SourceLabels.Add(entry.Name);
                    var name = Unique(entry.Name, entry.Directory, used);
                    Scan(entry, name, 0);
                }
                foreach (var path in job.Sources.Append(destination).Where(path => !TransferStorage.IsShell(path)))
                {
                    var root = VolumeIdentity.Root(path);
                    if (job.Volumes.All(volume => !volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                        job.Volumes.Add(new VolumeBinding { Root = root, Identity = VolumeIdentity.Get(root) ?? throw new IOException("Drive identity is unavailable.") });
                }
                job.Preflight = Inspect(job, storage, token);
                if (!job.Preflight.CanStart) throw new TransferPreflightException(job.Preflight);
                job.Status = TransferStatus.Queued; Validate(job); SaveManifest(job);
                lock (sync) jobs.Add(job.Id, job);
                Publish(job); return Snapshot(job);

                void Scan(StorageEntry entry, string relative, int depth)
                {
                    token.ThrowIfCancellationRequested();
                    if (depth > 512 || !seen.Add(entry.Location)) throw new IOException("A recursive or duplicate device selection was found.");
                    TransferStorage.SafeName(entry.Name);
                    if (entry.Directory)
                    {
                        job.Directories.Add(new() { Source = entry.Location, Relative = relative });
                        foreach (var child in storage.Children(entry.Location)) Scan(child, relative + "\\" + child.Name, depth + 1);
                    }
                    else
                    {
                        if (!TransferStorage.IsShell(entry.Location)) VolumeIdentity.RejectNamedStreamsForMove(entry.Location);
                        job.Files.Add(new() { Source = entry.Location, Relative = relative, Length = entry.Length, Modified = entry.Modified });
                    }
                }
            }).ConfigureAwait(false);
        }
        finally { planning.Release(); }
    }
    public Task RunAsync(Guid id, CancellationToken token) => NamespaceCom.Sta(() =>
    {
        NamespaceJob job; lock (sync) job = jobs[id];
        lock (job.Sync) { if (job.Executing || job.Status == TransferStatus.Completed) return false; job.Executing = true; }
        try
        {
            using var storage = StorageFactory();
            if (!Resolve(job)) throw new IOException("Reconnect the original source and destination drives.");
            token.ThrowIfCancellationRequested();
            job.Preflight = Inspect(job, storage, token);
            if (!job.Preflight.CanStart) throw new TransferPreflightException(job.Preflight);
            Status(job, TransferStatus.Running);
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = job.Destination };
            foreach (var folder in job.Directories)
            {
                token.ThrowIfCancellationRequested();
                var parent = folders[Parent(folder.Relative)]; var name = Leaf(folder.Relative);
                var existing = storage.Find(parent, name);
                if (existing is not null && !existing.Directory) throw new IOException("A file now occupies a planned folder: " + name);
                folders[folder.Relative] = existing?.Location ?? storage.CreateFolder(parent, name);
            }
            var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
            var compare = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
            try
            {
                for (int index = 0; index < job.Files.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); var file = job.Files[index]; if (file.Completed) continue;
                    lock (job.Sync) job.Current = file.Relative;
                    Publish(job);
                    Copy(job, file, index, folders[Parent(file.Relative)], storage, buffer, compare, token);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); ArrayPool<byte>.Shared.Return(compare); }
            storage.ReleaseFolderLocks();
            if (job.Operation == TransferOperation.Move)
                foreach (var folder in job.Directories.AsEnumerable().Reverse())
                {
                    token.ThrowIfCancellationRequested();
                    try { if (storage.Children(folder.Source).Count == 0) storage.Delete(folder.Source); }
                    catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
                }
            Status(job, TransferStatus.Completed);
        }
        catch (OperationCanceledException) { Status(job, TransferStatus.Paused); }
        catch (Exception error) when (error is IOException or COMException or UnauthorizedAccessException or NotSupportedException or CryptographicException or ArgumentException)
        {
            LastFailure = error;
            var ready = ReadyOnSta(job);
            Status(job, ready ? TransferStatus.Failed : TransferStatus.WaitingForDrive, error.Message);
        }
        finally { lock (job.Sync) { job.Executing = false; job.Current = null; } Publish(job); }
        return true;
    });
    public Task<bool> AvailableAsync(Guid id)
    {
        NamespaceJob job; lock (sync) job = jobs[id];
        return NamespaceCom.Sta(() => ReadyOnSta(job));
    }
    private bool ReadyOnSta(NamespaceJob job)
    {
        try
        {
            if (!Resolve(job)) return false;
            using var storage = StorageFactory(); storage.Stat(job.Destination);
            var pending = job.Files.FirstOrDefault(file => !file.Completed && !file.Committed);
            if (pending is not null) storage.Stat(pending.Source);
            return true;
        }
        catch (Exception error) when (error is IOException or COMException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public Task RemoveAsync(Guid id)
    {
        NamespaceJob job; lock (sync) job = jobs[id];
        return NamespaceCom.Sta(() =>
        {
            lock (job.Sync) { if (job.Executing) throw new IOException("Pause the transfer first."); }
            // Partial data is kept until its original destination is available, never guessed by name elsewhere.
            if (job.Status != TransferStatus.Completed)
            {
                if (!Resolve(job)) throw new IOException("Reconnect the destination before removing partial files.");
                using var storage = StorageFactory();
                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = job.Destination };
                foreach (var folder in job.Directories)
                    if (folders.TryGetValue(Parent(folder.Relative), out var parent) && storage.Find(parent, Leaf(folder.Relative)) is { Directory: true } found)
                        folders[folder.Relative] = found.Location;
                for (int index = 0; index < job.Files.Count; index++)
                {
                    var file = job.Files[index];
                    if (file.Completed || file.Committed || !folders.TryGetValue(Parent(file.Relative), out var parent)) continue;
                    // A WPD item can keep its ID after rename. Never delete a stored ID blindly:
                    // a crash after publish might leave that ID referring to the completed destination.
                    var partial = storage.Find(parent, $".stransfer-{job.Id:N}-{index}.partial");
                    if (partial is { Directory: false }) storage.Delete(partial.Location);
                }
            }
            File.Delete(Manifest(job)); File.Delete(Journal(job)); lock (sync) jobs.Remove(id); return true;
        });
    }
    private TransferPreflight Inspect(NamespaceJob job, TransferStorage storage, CancellationToken token)
    {
        var issues = new List<string>(); var capacity = storage.Capacity(job.Destination); long required = 0;
        foreach (var file in job.Files.Where(file => !file.Completed))
        {
            token.ThrowIfCancellationRequested();
            if (file.Committed) continue; // Recovery may follow source deletion before its final journal append.
            var entry = storage.Stat(file.Source);
            if (entry.Directory || entry.Length != file.Length || entry.Modified != file.Modified) issues.Add("Source changed: " + file.Relative);
            if (!TransferStorage.IsShell(file.Source)) VolumeIdentity.RejectNamedStreamsForMove(file.Source);
            var remaining = TransferStorage.IsShell(job.Destination) ? file.Length : Math.Max(0, file.Length - file.Offset);
            required = checked(required + ((remaining + capacity.ClusterBytes - 1) / capacity.ClusterBytes) * capacity.ClusterBytes);
            if (capacity.Format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && file.Length > uint.MaxValue) issues.Add("FAT32 cannot store this file: " + file.Relative);
        }
        required = checked(required + (long)job.Directories.Count * capacity.ClusterBytes);
        var reserved = OtherReservations(job.Id, job.Destination);
        var margin = Math.Clamp(required / 100, 4L * 1024 * 1024, 256L * 1024 * 1024);
        if (capacity.FreeBytes < checked(required + margin + reserved)) issues.Add("Insufficient destination space. Free space must cover this job, other queued jobs and the safety margin.");
        storage.CheckWritable(job.Destination);
        return new() { RequiredBytes = required, AvailableBytes = capacity.FreeBytes, SafetyMarginBytes = margin,
            ReservedByQueuedJobsBytes = reserved, FileCount = job.Files.Count, FileSystem = capacity.Format, Issues = issues };
    }
    private void Status(NamespaceJob job, TransferStatus status, string? error = null)
    {
        lock (job.Sync) { job.Status = status; job.Error = error; job.Current = null; }
        Append(job, null); Publish(job);
    }
    private void Publish(NamespaceJob job, bool force = false)
    {
        if (!force && job.Status == TransferStatus.Running && job.PublicationClock.ElapsedMilliseconds < 150) return;
        job.PublicationClock.Restart();
        var snapshot = Snapshot(job);
        if (JobChanged is { } handlers) foreach (EventHandler<TransferJobSnapshot> handler in handlers.GetInvocationList()) try { handler(this, snapshot); } catch { }
    }
    private static TransferJobSnapshot Snapshot(NamespaceJob job)
    {
        lock (job.Sync) return new() { Id = job.Id, Operation = job.Operation, Status = job.Status,
            DestinationDirectory = job.Destination, DestinationDisplayName = job.DestinationLabel, Sources = job.SourceLabels.ToArray(), CreatedAt = job.Created, UpdatedAt = job.Updated,
            TotalFiles = job.Files.Count, CompletedFiles = job.Files.Count(file => file.Completed), TotalBytes = job.Files.Sum(file => file.Length),
            BytesTransferred = job.Files.Sum(file => file.Completed ? file.Length : file.Offset), CurrentFile = job.Current, Error = job.Error,
            Verify = job.Verify, Preflight = job.Preflight, SmallFileWorkers = 1 };
    }
    private static bool Within(string path, string root)
    {
        var a = TransferStorage.Display(path).TrimEnd('\\'); var b = TransferStorage.Display(root).TrimEnd('\\');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }
    private static string Unique(string name, bool folder, HashSet<string> used)
    {
        TransferStorage.SafeName(name); var extension = folder ? "" : Path.GetExtension(name); var stem = folder ? name : Path.GetFileNameWithoutExtension(name);
        var result = name;
        for (int number = 2; !used.Add(result); number++)
        {
            var suffix = $" ({number})"; var length = Math.Min(stem.Length, 255 - extension.Length - suffix.Length);
            if (length < 1) throw new IOException("File name is too long to keep both copies.");
            result = stem[..length] + suffix + extension;
        }
        return result;
    }
    private static string Parent(string relative) { int index = relative.LastIndexOf('\\'); return index < 0 ? "" : relative[..index]; }
    private static string Leaf(string relative) => relative[(relative.LastIndexOf('\\') + 1)..];
    public void Dispose() { planning.Dispose(); }
}

internal sealed class NamespaceJob
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; }
    public Guid Epoch { get; set; } = Guid.NewGuid();
    public TransferOperation Operation { get; set; }
    public TransferStatus Status { get; set; }
    public bool Verify { get; set; }
    public string Destination { get; set; } = "";
    public string DestinationLabel { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public List<string> SourceLabels { get; set; } = [];
    public List<NamespaceFile> Files { get; set; } = [];
    public List<NamespaceFolder> Directories { get; set; } = [];
    public List<VolumeBinding> Volumes { get; set; } = [];
    public string? Error { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    [JsonIgnore] public string? Current { get; set; }
    [JsonIgnore] public bool Executing { get; set; }
    [JsonIgnore] public object Sync { get; } = new();
    [JsonIgnore] public TransferPreflight? Preflight { get; set; }
    [JsonIgnore] public Stopwatch PublicationClock { get; } = Stopwatch.StartNew();
}
internal sealed class NamespaceFolder
{
    public string Source { get; set; } = "";
    public string Relative { get; set; } = "";
}
internal sealed class NamespaceFile
{
    public string Source { get; set; } = "";
    public string Relative { get; set; } = "";
    public long Length { get; set; }
    public long Modified { get; set; }
    public string? Partial { get; set; }
    public long Offset { get; set; }
    public string? Hash { get; set; }
    public bool Ready { get; set; }
    public bool Committed { get; set; }
    public bool Completed { get; set; }
}
