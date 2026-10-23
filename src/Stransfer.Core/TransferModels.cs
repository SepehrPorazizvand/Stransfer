namespace Stransfer.Core;

public enum TransferOperation { Copy, Move }
public enum TransferStatus { Queued, Scanning, Running, Paused, WaitingForDrive, Completed, Failed, Canceled }

public sealed record TransferOptions
{
    /// <summary>0 detects non-seeking local drives; otherwise use 1, 2, 4 or 8 workers for files below 8 MiB.</summary>
    public int SmallFileWorkers { get; init; }
}

public sealed record TransferPreflight
{
    public long RequiredBytes { get; init; }
    public long AvailableBytes { get; init; }
    public long SafetyMarginBytes { get; init; }
    public long ReservedByQueuedJobsBytes { get; init; }
    public int FileCount { get; init; }
    public string FileSystem { get; init; } = "Unknown";
    public IReadOnlyList<string> Issues { get; init; } = [];
    public bool CanStart => Issues.Count == 0;
}

public sealed class TransferPreflightException(TransferPreflight report)
    : IOException(string.Join(Environment.NewLine, report.Issues))
{
    public TransferPreflight Report { get; } = report;
}

public sealed record TransferJobSnapshot
{
    public Guid Id { get; init; }
    public TransferOperation Operation { get; init; }
    public TransferStatus Status { get; init; }
    public string DestinationDirectory { get; init; } = "";
    public string? DestinationDisplayName { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public long TotalBytes { get; init; }
    public long BytesTransferred { get; init; }
    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public string? CurrentFile { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool Verify { get; init; }
    public TransferPreflight? Preflight { get; init; }
    public int SmallFileWorkers { get; init; }
    public bool CanResume => Status is TransferStatus.Paused or TransferStatus.WaitingForDrive or TransferStatus.Failed or TransferStatus.Queued;
}

internal sealed class JobState
{
    public int Version { get; set; } = 1;
    public Guid JournalEpoch { get; set; } = Guid.NewGuid();
    public Guid Id { get; set; }
    public TransferOperation Operation { get; set; }
    public TransferStatus Status { get; set; }
    public string DestinationDirectory { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public List<string> SourceDirectories { get; set; } = [];
    public List<string> DestinationDirectories { get; set; } = [];
    public List<FilePlan> Files { get; set; } = [];
    public List<VolumeBinding> Volumes { get; set; } = [];
    public string? Error { get; set; }
    public string? CurrentFile { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Verify { get; set; }
    public TransferOptions Options { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public TransferPreflight? Preflight { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Queue<PendingJournalEntry> PendingJournal { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public bool JournalWriterRunning { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public TaskCompletionSource? JournalBatchReady { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int EffectiveWorkers { get; set; } = 1;
    [System.Text.Json.Serialization.JsonIgnore] public DirectoryGuard? DirectoryGuard { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public object Sync { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource? Cancellation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsExecuting { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long TotalBytes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long BytesTransferred { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int CompletedFiles { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ManifestSaved { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long ManifestLength { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<string> SourceSnapshot { get; set; } = [];
}

internal sealed record PendingJournalEntry(ProgressEntry Entry, TaskCompletionSource Completion);

internal sealed class FilePlan
{
    [System.Text.Json.Serialization.JsonIgnore] public int Index { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Partial { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteTicks { get; set; }
    public long CreationTicks { get; set; }
    public FileAttributes Attributes { get; set; }
    public long Offset { get; set; }
    public long DurableOffset { get; set; }
    public bool ReadyToCommit { get; set; }
    public bool DestinationCommitted { get; set; }
    public bool Completed { get; set; }
    public bool AtomicMove { get; set; }
    public string? VerifiedHash { get; set; }
    public string? SourceFileIdentity { get; set; }
}

internal sealed class VolumeBinding
{
    public string Root { get; set; } = "";
    public string Identity { get; set; } = "";
}

internal sealed class ProgressEntry
{
    public Guid Epoch { get; set; }
    public TransferStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int? FileIndex { get; set; }
    public long DurableOffset { get; set; }
    public bool ReadyToCommit { get; set; }
    public bool DestinationCommitted { get; set; }
    public bool Completed { get; set; }
    public bool AtomicMove { get; set; }
    public string? VerifiedHash { get; set; }
    public string? SourceFileIdentity { get; set; }
}
