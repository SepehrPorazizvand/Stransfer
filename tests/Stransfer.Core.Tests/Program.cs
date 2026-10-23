using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Stransfer.Core;

if (args is ["--crash-worker", var workerState, var workerSignal])
{
    using var worker = new TransferManager(workerState);
    var pending = worker.Jobs.Single();
    worker.JobChanged += (_, progress) =>
    {
        if (progress.Status == TransferStatus.Running && progress.BytesTransferred >= 40L * 1024 * 1024)
        {
            File.WriteAllText(workerSignal, "ready to terminate");
            // Freeze inside an active copy so the parent can terminate this process,
            // without giving Pause/Dispose an opportunity to save a graceful stop.
            Thread.Sleep(Timeout.Infinite);
        }
    };
    await worker.RunAsync(pending.Id);
    return 3;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("copy directory, nested content, empty directories, and zero-byte files", CopyTree),
    ("move verifies and removes only a successful source", MoveFile),
    ("same-volume native move preserves named NTFS streams", AtomicMovePreservesNamedStreams),
    ("resumed move verifies a partial copy before deleting its source", ResumedVerifiedMove),
    ("copy-delete move rejects named streams and retains the complete source", CopyDeleteRejectsNamedStreams),
    ("directory move rejects named directory streams before acceptance", DirectoryMoveRejectsNamedStreams),
    ("existing destination is preserved with keep-both naming", KeepBoth),
    ("long destination names keep both files within the component limit", LongFilename),
    ("a destination occupied after planning cannot be overwritten", LateCollision),
    ("directory cannot be copied into itself or its descendants", PreventRecursion),
    ("overlapping source selections move each file only once", OverlappingSources),
    ("paused transfer recovers through an incomplete journal tail", PauseAndRecover),
    ("abruptly killed copy recovers its persisted checkpoint", AbruptProcessRecovery),
    ("external cancellation leaves a resumable job", CancellationPauses),
    ("pausing a move keeps its source", PauseMove),
    ("modified source cannot be merged into an old partial file", ChangedSource),
    ("missing partial file resumes by copying from the beginning", MissingPartial),
    ("missing source fails without publishing a final destination", MissingSource),
    ("one malformed persisted record does not hide healthy jobs", MalformedRecord),
    ("a different volume is rejected until the original identity returns", WrongVolume),
    ("preflight rejects insufficient allocation space before accepting a job", InsufficientSpace),
    ("queued and resumed transfers recheck free space before writing", RecheckSpace),
    ("FAT32 preflight rejects a real oversized sparse source before acceptance", Fat32Limit),
    ("parallel tiny files keep content, names and durable recovery records", ParallelSmallFiles),
    ("parallel pause retains sources and resumes verified copies", ParallelPause),
    ("copy preflight refuses alternate streams instead of silently dropping them", CopyRejectsNamedStreams),
    ("queued jobs reserve destination names before either transfer starts", QueuedNames),
    ("queued allocation is included before accepting another transfer", QueuedSpace),
    ("parallel journal survives a real worker-process termination", ParallelAbruptRecovery),
    ("copy preserves creation/modification time and basic attributes", CopyMetadata),
    ("durable publish intent recovers when completion records are lost", LostCompletionRecord),
};

var failed = 0;
var selectedTests = args is ["--test", var filter] ? tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray() : tests;
foreach (var test in selectedTests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(90));
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}\n{error}");
    }
}

Console.WriteLine($"\n{selectedTests.Length - failed}/{selectedTests.Length} integration tests passed.");
return failed == 0 ? 0 : 1;

static async Task CopyTree()
{
    using var sandbox = new Sandbox();
    var source = sandbox.Directory("source", "Album");
    Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
    var first = Path.Combine(source, "photo.bin");
    var second = Path.Combine(source, "nested", "sound.bin");
    await WriteData(first, 1_111_117, 7);
    await WriteData(second, 222_229, 17);
    await File.WriteAllBytesAsync(Path.Combine(source, "empty.txt"), []);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    await manager.RunAsync(job.Id);
    var final = GetJob(manager, job.Id);
    Equal(TransferStatus.Completed, final.Status, final.Error);
    var target = Path.Combine(sandbox.Destination, "Album");
    await EqualFiles(first, Path.Combine(target, "photo.bin"));
    await EqualFiles(second, Path.Combine(target, "nested", "sound.bin"));
    Require(Directory.Exists(Path.Combine(target, "nested", "empty")), "Empty nested directory was lost.");
    Equal(0L, new FileInfo(Path.Combine(target, "empty.txt")).Length);
    Require(File.Exists(first) && File.Exists(second), "Copy removed an original file.");
    Require(!Directory.EnumerateFiles(sandbox.Destination, "*.stransfer-*", SearchOption.AllDirectories).Any(),
        "A successful job left temporary transfer files.");
}

static async Task MoveFile()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "move.bin");
    await WriteData(source, 2_000_009, 3);
    var expected = await Hash(source);
    var manager = new TransferManager(sandbox.State);
    // Moves must still verify their data when the optional copy-verification setting is off.
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move, verify: false);
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status);
    Require(!File.Exists(source), "Successful move left its source behind.");
    Equal(expected, await Hash(Path.Combine(sandbox.Destination, "move.bin")));
}

static async Task KeepBoth()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "report.bin");
    var existing = Path.Combine(sandbox.Destination, "report.bin");
    await WriteData(source, 55_557, 23);
    await WriteData(existing, 77_779, 41);
    var oldHash = await Hash(existing);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status);
    Equal(oldHash, await Hash(existing), "An existing destination was overwritten.");
    var files = Directory.GetFiles(sandbox.Destination);
    Equal(2, files.Length, "Keep-both should leave exactly the original and the new file.");
    await EqualFiles(source, files.Single(path => !StringComparer.OrdinalIgnoreCase.Equals(path, existing)));
}

static async Task ResumedVerifiedMove()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "verified-move.bin");
    await WriteData(source, 4 * 1024 * 1024 + 19, 67);
    var expected = await Hash(source);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move, verify: false);
    manager.Dispose();

    await SeedPartialCheckpoint(sandbox.State, job.Id, source);

    using var recovered = new TransferManager(sandbox.State);
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(recovered, job.Id).Status, GetJob(recovered, job.Id).Error);
    Equal(expected, await Hash(Path.Combine(sandbox.Destination, "verified-move.bin")));
    Require(!File.Exists(source), "Verified-copy move did not remove the verified source.");
}

static async Task AtomicMovePreservesNamedStreams()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "named-stream.bin");
    await WriteData(source, 100_043, 103);
    const string streamContent = "Stransfer alternate stream content";
    await File.WriteAllTextAsync(source + ":StransferTest", streamContent);
    var expected = await Hash(source);
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move);
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status, GetJob(manager, job.Id).Error);
    var destination = Path.Combine(sandbox.Destination, "named-stream.bin");
    Equal(expected, await Hash(destination));
    Equal(streamContent, await File.ReadAllTextAsync(destination + ":StransferTest"));
    Require(!File.Exists(source), "Native named-stream move did not remove the old source path.");
}

static async Task CopyDeleteRejectsNamedStreams()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "retain-stream.bin");
    await WriteData(source, 4 * 1024 * 1024 + 23, 107);
    const string streamContent = "This alternate stream must not be lost by copy/delete.";
    await File.WriteAllTextAsync(source + ":StransferTest", streamContent);
    var expected = await Hash(source);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move, verify: false);
    manager.Dispose();
    await SeedPartialCheckpoint(sandbox.State, job.Id, source);
    using var recovered = new TransferManager(sandbox.State);
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Failed, GetJob(recovered, job.Id).Status, GetJob(recovered, job.Id).Error);
    Equal(expected, await Hash(source), "Rejected named-stream move changed its source contents.");
    Equal(streamContent, await File.ReadAllTextAsync(source + ":StransferTest"));
    Require(!File.Exists(Path.Combine(sandbox.Destination, "retain-stream.bin")),
        "An unsupported named-stream copy/delete move published a final destination.");
}

static async Task SeedPartialCheckpoint(string stateDirectory, Guid jobId, string source)
{
    // Seed a valid version-1 paused checkpoint so a local-volume test exercises
    // the same copy/verify/delete path as a cross-volume Move, rather than rename.
    var recordPath = Path.Combine(stateDirectory, jobId.ToString("N") + ".json");
    var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!.AsObject();
    var file = record["Files"]!.AsArray().Single()!.AsObject();
    var partialPath = file["Partial"]!.GetValue<string>();
    var prefix = new byte[1024 * 1024];
    await using (var input = File.OpenRead(source)) await input.ReadExactlyAsync(prefix);
    await File.WriteAllBytesAsync(partialPath, prefix);
    file["Offset"] = prefix.Length;
    file["DurableOffset"] = prefix.Length;
    record["Status"] = (int)TransferStatus.Paused;
    await File.WriteAllTextAsync(recordPath, record.ToJsonString());
    File.Delete(Path.Combine(stateDirectory, jobId.ToString("N") + ".journal"));
}

static async Task DirectoryMoveRejectsNamedStreams()
{
    using var sandbox = new Sandbox();
    var folder = sandbox.Directory("source", "stream-folder");
    var child = Path.Combine(folder, "child.bin");
    await WriteData(child, 10_049, 109);
    const string streamContent = "Directory metadata stream must be retained.";
    await File.WriteAllTextAsync(folder + ":StransferTest", streamContent);
    var expected = await Hash(child);
    using var manager = new TransferManager(sandbox.State);
    var rejected = false;
    try { await manager.CreateJobAsync([folder], sandbox.Destination, TransferOperation.Move); }
    catch (NotSupportedException) { rejected = true; }
    Require(rejected, "Directory Move accepted named directory streams it cannot preserve.");
    Equal(0, manager.Jobs.Count);
    Equal(expected, await Hash(child));
    Equal(streamContent, await File.ReadAllTextAsync(folder + ":StransferTest"));
    Require(!System.IO.Directory.EnumerateFileSystemEntries(sandbox.Destination).Any(),
        "Rejected directory Move wrote destination data.");
}

static async Task LongFilename()
{
    using var sandbox = new Sandbox();
    var name = new string('n', 251) + ".bin";
    var source = Path.Combine(sandbox.Source, name);
    var existing = Path.Combine(sandbox.Destination, name);
    await WriteData(source, 30_017, 89);
    await WriteData(existing, 40_031, 97);
    var oldHash = await Hash(existing);
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status, GetJob(manager, job.Id).Error);
    Equal(oldHash, await Hash(existing), "Long-name collision overwrote the existing file.");
    var files = System.IO.Directory.GetFiles(sandbox.Destination);
    Equal(2, files.Length);
    Require(files.All(path => Path.GetFileName(path).Length <= 255), "Keep-both exceeded the filename component limit.");
    var copied = files.Single(path => !StringComparer.OrdinalIgnoreCase.Equals(path, existing));
    Equal(".bin", Path.GetExtension(copied));
    await EqualFiles(source, copied);
}

static async Task LateCollision()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "late-collision.bin");
    var destination = Path.Combine(sandbox.Destination, "late-collision.bin");
    await WriteData(source, 30_011, 73);
    var sourceHash = await Hash(source);
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move);
    await WriteData(destination, 40_009, 79);
    var destinationHash = await Hash(destination);
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Failed, GetJob(manager, job.Id).Status);
    Equal(sourceHash, await Hash(source), "Late collision removed or changed the source.");
    Equal(destinationHash, await Hash(destination), "Late collision overwrote an unrelated destination.");
}

static async Task PreventRecursion()
{
    using var sandbox = new Sandbox();
    var source = sandbox.Directory("source", "tree");
    await WriteData(Path.Combine(source, "file.bin"), 17, 1);
    var manager = new TransferManager(sandbox.State);
    await ThrowsValidation(() => manager.CreateJobAsync([source], source));
    await ThrowsValidation(() => manager.CreateJobAsync([source], Path.Combine(source, "descendant")));
}

static async Task OverlappingSources()
{
    using var sandbox = new Sandbox();
    var folder = sandbox.Directory("source", "overlap");
    var source = Path.Combine(folder, "child.bin");
    await WriteData(source, 100_019, 83);
    var expected = await Hash(source);
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source, folder], sandbox.Destination, TransferOperation.Move);
    Equal(1, job.TotalFiles, "A selected folder and its selected child should produce one file plan.");
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status, GetJob(manager, job.Id).Error);
    Require(!System.IO.Directory.Exists(folder), "An overlapping move left its empty source tree.");
    Equal(expected, await Hash(Path.Combine(sandbox.Destination, "overlap", "child.bin")));
}

static async Task PauseAndRecover()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "recover.bin");
    await WriteData(source, 64 * 1024 * 1024 + 13, 19);
    var expected = await Hash(source);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    var pauseRequested = false;
    manager.JobChanged += (_, progress) =>
    {
        if (progress.Id == job.Id && progress.Status == TransferStatus.Running && progress.BytesTransferred > 0 && !pauseRequested)
        {
            pauseRequested = true;
            manager.Pause(job.Id);
        }
    };
    await manager.RunAsync(job.Id);
    Require(pauseRequested, "No running notification was published.");
    Equal(TransferStatus.Paused, GetJob(manager, job.Id).Status);
    Require(File.Exists(source), "A paused copy removed its source.");
    var checkpointBytes = GetJob(manager, job.Id).BytesTransferred;
    manager.Dispose();
    await File.AppendAllTextAsync(Path.Combine(sandbox.State, job.Id.ToString("N") + ".journal"), "{\"Epoch\":");
    var recovered = new TransferManager(sandbox.State);
    var restored = GetJob(recovered, job.Id);
    Equal(TransferStatus.Paused, restored.Status);
    Require(restored.CanResume, "Recovered job was not resumable.");
    Equal(checkpointBytes, restored.BytesTransferred, "An incomplete journal tail discarded valid durable progress.");
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(recovered, job.Id).Status);
    Require(File.Exists(source), "Resumed copy removed its source.");
    Equal(64L * 1024 * 1024 + 13, new FileInfo(Path.Combine(sandbox.Destination, "recover.bin")).Length);
    Equal(expected, await Hash(Path.Combine(sandbox.Destination, "recover.bin")));
}

static async Task CancellationPauses()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "cancel.bin");
    await WriteData(source, 8 * 1024 * 1024 + 1, 29);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    using var cancellation = new CancellationTokenSource();
    manager.JobChanged += (_, progress) =>
    {
        if (progress.Id == job.Id && progress.Status == TransferStatus.Running)
            cancellation.Cancel();
    };
    await manager.RunAsync(job.Id, cancellation.Token);
    Equal(TransferStatus.Paused, GetJob(manager, job.Id).Status);
    Require(File.Exists(source), "Canceled copy removed a source.");
    manager.Dispose();
    var recovered = new TransferManager(sandbox.State);
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(recovered, job.Id).Status);
    await EqualFiles(source, Path.Combine(sandbox.Destination, "cancel.bin"));
}

static async Task AbruptProcessRecovery()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "process-kill.bin");
    await WriteData(source, 64 * 1024 * 1024 + 17, 61);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    manager.Dispose();
    var signal = Path.Combine(sandbox.State, "worker-ready");
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Sandbox).Assembly.Location);
    start.ArgumentList.Add("--crash-worker");
    start.ArgumentList.Add(sandbox.State);
    start.ArgumentList.Add(signal);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start recovery test worker.");
    try
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(signal) && !process.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(20);
        Require(File.Exists(signal), "Copy worker did not reach its checkpoint before exiting or timing out.");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    using var recovered = new TransferManager(sandbox.State);
    var restored = GetJob(recovered, job.Id);
    Equal(TransferStatus.Paused, restored.Status, "An interrupted running job should ask before resuming.");
    Require(restored.CanResume, "An abruptly interrupted job was not resumable.");
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(recovered, job.Id).Status);
    await EqualFiles(source, Path.Combine(sandbox.Destination, "process-kill.bin"));
}

static async Task MissingSource()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "gone.bin");
    await WriteData(source, 10_013, 37);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move);
    File.Delete(source);
    await manager.RunAsync(job.Id);
    var result = GetJob(manager, job.Id);
    Require(result.Status is TransferStatus.Failed or TransferStatus.WaitingForDrive,
        $"Missing source unexpectedly returned {result.Status}.");
    Require(!File.Exists(Path.Combine(sandbox.Destination, "gone.bin")), "Missing source published a final destination.");
}

static async Task PauseMove()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "paused-move.bin");
    await WriteData(source, 1_000_003, 43);
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move);
    manager.JobChanged += (_, progress) =>
    {
        if (progress.Id == job.Id && progress.Status == TransferStatus.Running)
            manager.Pause(job.Id);
    };
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Paused, GetJob(manager, job.Id).Status);
    Require(File.Exists(source), "Paused move deleted the source before it completed.");
}

static async Task MalformedRecord()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "healthy.bin");
    await WriteData(source, 10_019, 71);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    manager.Dispose();
    var healthyPath = Path.Combine(sandbox.State, job.Id.ToString("N") + ".json");
    var malformed = JsonNode.Parse(await File.ReadAllTextAsync(healthyPath))!.AsObject();
    var corruptId = Guid.NewGuid();
    malformed["Id"] = corruptId;
    malformed["Sources"] = null;
    await File.WriteAllTextAsync(Path.Combine(sandbox.State, corruptId.ToString("N") + ".json"), malformed.ToJsonString());
    using var recovered = new TransferManager(sandbox.State);
    Equal(1, recovered.Jobs.Count);
    Equal(job.Id, recovered.Jobs.Single().Id);
    Require(recovered.RecoveryWarnings.Count > 0, "Malformed state was ignored without a recovery warning.");
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(recovered, job.Id).Status);
    await EqualFiles(source, Path.Combine(sandbox.Destination, "healthy.bin"));
}

static async Task WrongVolume()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "identity.bin");
    await WriteData(source, 40_037, 101);
    var sourceHash = await Hash(source);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination, TransferOperation.Move);
    manager.Dispose();
    var recordPath = Path.Combine(sandbox.State, job.Id.ToString("N") + ".json");
    var record = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!.AsObject();
    var binding = record["Volumes"]!.AsArray().Single()!.AsObject();
    var originalIdentity = binding["Identity"]!.GetValue<string>();
    binding["Identity"] = "volume:unavailable-test-" + Guid.NewGuid().ToString("N");
    await File.WriteAllTextAsync(recordPath, record.ToJsonString());
    using (var wrong = new TransferManager(sandbox.State))
    {
        Require(!wrong.AreDrivesAvailable(job.Id), "A different volume was accepted for the original transfer.");
        await wrong.RunAsync(job.Id);
        Equal(TransferStatus.WaitingForDrive, GetJob(wrong, job.Id).Status);
        Equal(sourceHash, await Hash(source), "Wrong-volume job changed its source.");
        Require(!System.IO.Directory.EnumerateFileSystemEntries(sandbox.Destination).Any(),
            "Wrong-volume job wrote data before the original volume became available.");
    }
    binding["Identity"] = originalIdentity;
    await File.WriteAllTextAsync(recordPath, record.ToJsonString());
    using var reconnected = new TransferManager(sandbox.State);
    Require(reconnected.AreDrivesAvailable(job.Id), "Restored original identity was not recognized.");
    await reconnected.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(reconnected, job.Id).Status, GetJob(reconnected, job.Id).Error);
    Equal(sourceHash, await Hash(Path.Combine(sandbox.Destination, "identity.bin")));
    Require(!File.Exists(source), "Successful resumed move did not remove the original source.");
}

static async Task ChangedSource()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "changed.bin");
    await WriteData(source, 16 * 1024 * 1024 + 7, 47);
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    EventHandler<TransferJobSnapshot> pause = (_, progress) =>
    {
        if (progress.Id == job.Id && progress.Status == TransferStatus.Running && progress.BytesTransferred > 0)
            manager.Pause(job.Id);
    };
    manager.JobChanged += pause;
    await manager.RunAsync(job.Id);
    manager.JobChanged -= pause;
    Equal(TransferStatus.Paused, GetJob(manager, job.Id).Status);
    await using (var input = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.None))
        await input.WriteAsync(new byte[] { 42 });
    var expected = await Hash(source);
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Failed, GetJob(manager, job.Id).Status);
    Equal(expected, await Hash(source), "Failed transfer modified its changed source.");
    Require(!File.Exists(Path.Combine(sandbox.Destination, "changed.bin")), "A changed source was published as complete.");
}

static async Task MissingPartial()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "lost-partial.bin");
    await WriteData(source, 16 * 1024 * 1024 + 9, 53);
    var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    manager.JobChanged += (_, progress) =>
    {
        if (progress.Id == job.Id && progress.Status == TransferStatus.Running && progress.BytesTransferred > 0)
            manager.Pause(job.Id);
    };
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Paused, GetJob(manager, job.Id).Status);
    manager.Dispose();
    var partials = System.IO.Directory.GetFiles(sandbox.Destination);
    Require(partials.Length > 0, "Pause did not leave a partial file to recover.");
    foreach (var partial in partials) File.Delete(partial);
    using var recovered = new TransferManager(sandbox.State);
    await recovered.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(recovered, job.Id).Status);
    await EqualFiles(source, Path.Combine(sandbox.Destination, "lost-partial.bin"));
}

static TransferJobSnapshot GetJob(TransferManager manager, Guid id) => manager.Jobs.Single(job => job.Id == id);

static async Task InsufficientSpace()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "small.txt");
    await File.WriteAllTextAsync(source, "hello");
    using var manager = new TransferManager(sandbox.State) { ReadSpace = _ => new StorageSpace(4095, 4096, "NTFS") };
    try { await manager.CreateJobAsync([source], sandbox.Destination); throw new InvalidOperationException("An insufficient-space job was accepted."); }
    catch (TransferPreflightException error)
    {
        Require(error.Report.RequiredBytes >= 4096 && !error.Report.CanStart, "Allocation rounding was not included.");
        Require(error.Report.AvailableBytes == 4095, "Available space was not reported.");
    }
    Equal(0, manager.Jobs.Count);
    Require(!Directory.EnumerateFileSystemEntries(sandbox.Destination).Any(), "Preflight left destination files.");
    Require(!Directory.EnumerateFiles(sandbox.State, "*.json").Any(), "A rejected job was persisted.");
}

static async Task RecheckSpace()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "space.txt");
    await File.WriteAllTextAsync(source, "keep this source");
    using var manager = new TransferManager(sandbox.State);
    var job = await manager.CreateJobAsync([source], sandbox.Destination);
    manager.ReadSpace = _ => new StorageSpace(0, 4096, "NTFS");
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Failed, GetJob(manager, job.Id).Status);
    Require(!Directory.EnumerateFileSystemEntries(sandbox.Destination).Any(), "Space failure began writing files.");
    var check = await manager.CheckAsync(job.Id);
    Require(!check.CanStart, "The preflight API accepted zero space.");
    manager.ReadSpace = StorageProbe.Read;
    await manager.RunAsync(job.Id);
    Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status);
    await EqualFiles(source, Path.Combine(sandbox.Destination, "space.txt"));
}

static async Task Fat32Limit()
{
    if (!OperatingSystem.IsWindows()) return;
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "oversized.bin");
    using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        Require(NativeSparse.SetSparse(file.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "Could not create a sparse test file.");
        file.SetLength(1L + uint.MaxValue);
    }
    using var manager = new TransferManager(sandbox.State) { ReadSpace = _ => new StorageSpace(long.MaxValue / 2, 4096, "FAT32") };
    try { await manager.CreateJobAsync([source], sandbox.Destination); throw new InvalidOperationException("FAT32 accepted a file larger than its limit."); }
    catch (TransferPreflightException error) { Require(error.Report.Issues.Any(issue => issue.Contains("FAT32")), "FAT32 limit was not identified."); }
    Equal(0, manager.Jobs.Count);
    Require(!Directory.EnumerateFileSystemEntries(sandbox.Destination).Any(), "An oversized file reached a destination.");
}

static async Task ParallelSmallFiles()
{
    using var sandbox = new Sandbox();
    for (var index = 0; index < 250; index++) await File.WriteAllTextAsync(Path.Combine(sandbox.Source, $"file-{index:D4}.txt"), $"File {index}: " + new string((char)('a' + index % 26), index * 3));
    Guid id;
    using (var manager = new TransferManager(sandbox.State))
    {
        var job = await manager.CreateJobAsync([sandbox.Source], sandbox.Destination, options: new TransferOptions { SmallFileWorkers = 4 });
        id = job.Id;
        await manager.RunAsync(id);
        Equal(TransferStatus.Completed, GetJob(manager, id).Status);
        Equal(250, GetJob(manager, id).CompletedFiles);
        foreach (var source in Directory.EnumerateFiles(sandbox.Source)) await EqualFiles(source, Path.Combine(sandbox.Destination, "source", Path.GetFileName(source)));
    }
    using var recovered = new TransferManager(sandbox.State);
    Equal(TransferStatus.Completed, GetJob(recovered, id).Status);
    Equal(250, GetJob(recovered, id).CompletedFiles);
    Equal(4, GetJob(recovered, id).SmallFileWorkers);
}

static async Task ParallelPause()
{
    using var sandbox = new Sandbox();
    for (var index = 0; index < 40; index++) await WriteData(Path.Combine(sandbox.Source, $"part-{index}.bin"), 256 * 1024, index);
    Guid id;
    using (var manager = new TransferManager(sandbox.State))
    {
        var job = await manager.CreateJobAsync([sandbox.Source], sandbox.Destination, options: new TransferOptions { SmallFileWorkers = 4 }); id = job.Id;
        manager.JobChanged += (_, progress) => { if (progress.CompletedFiles >= 2 && progress.Status == TransferStatus.Running) manager.Pause(id); };
        await manager.RunAsync(id);
        Equal(TransferStatus.Paused, GetJob(manager, id).Status);
        Equal(40, Directory.EnumerateFiles(sandbox.Source).Count());
    }
    using var recovered = new TransferManager(sandbox.State);
    await recovered.RunAsync(id);
    Equal(TransferStatus.Completed, GetJob(recovered, id).Status);
    foreach (var source in Directory.EnumerateFiles(sandbox.Source)) await EqualFiles(source, Path.Combine(sandbox.Destination, "source", Path.GetFileName(source)));
}

static async Task CopyRejectsNamedStreams()
{
    if (!OperatingSystem.IsWindows()) return;
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "stream.txt");
    await File.WriteAllTextAsync(source, "main");
    await File.WriteAllTextAsync(source + ":details", "metadata");
    using var manager = new TransferManager(sandbox.State);
    try { await manager.CreateJobAsync([source], sandbox.Destination); throw new InvalidOperationException("Copy silently accepted a stream it cannot preserve."); }
    catch (NotSupportedException) { }
    Equal("metadata", await File.ReadAllTextAsync(source + ":details"));
    Equal(0, manager.Jobs.Count);
}

static async Task QueuedNames()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "duplicate.txt"); await File.WriteAllTextAsync(source, "both copies");
    using var manager = new TransferManager(sandbox.State);
    var first = await manager.CreateJobAsync([source], sandbox.Destination);
    var second = await manager.CreateJobAsync([source], sandbox.Destination);
    await manager.RunAsync(first.Id); await manager.RunAsync(second.Id);
    Equal(TransferStatus.Completed, GetJob(manager, first.Id).Status); Equal(TransferStatus.Completed, GetJob(manager, second.Id).Status);
    await EqualFiles(source, Path.Combine(sandbox.Destination, "duplicate.txt"));
    await EqualFiles(source, Path.Combine(sandbox.Destination, "duplicate (2).txt"));
}

static async Task CopyMetadata()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "metadata.txt"); await File.WriteAllTextAsync(source, "metadata");
    var created = new DateTime(2021, 5, 3, 1, 2, 3, DateTimeKind.Utc);
    var modified = new DateTime(2022, 6, 4, 2, 3, 4, DateTimeKind.Utc);
    File.SetCreationTimeUtc(source, created); File.SetLastWriteTimeUtc(source, modified);
    File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
    var target = Path.Combine(sandbox.Destination, "metadata.txt");
    try
    {
        using var manager = new TransferManager(sandbox.State);
        var job = await manager.CreateJobAsync([source], sandbox.Destination); await manager.RunAsync(job.Id);
        Equal(TransferStatus.Completed, GetJob(manager, job.Id).Status, GetJob(manager, job.Id).Error);
        Equal(modified, File.GetLastWriteTimeUtc(target));
        if (OperatingSystem.IsWindows()) Equal(created, File.GetCreationTimeUtc(target));
        Require((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0, "Read-only attribute was lost.");
        await EqualFiles(source, target);
    }
    finally { if (File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal); if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal); }
}

static async Task LostCompletionRecord()
{
    foreach (var operation in new[] { TransferOperation.Copy, TransferOperation.Move })
    {
        using var sandbox = new Sandbox();
        var source = Path.Combine(sandbox.Source, "intent.txt"); await File.WriteAllTextAsync(source, "Recover the already published file.");
        var expected = await Hash(source);
        Guid id;
        using (var manager = new TransferManager(sandbox.State))
        {
            id = (await manager.CreateJobAsync([source], sandbox.Destination, operation)).Id;
            await manager.RunAsync(id); Equal(TransferStatus.Completed, GetJob(manager, id).Status);
        }
        var journal = Path.Combine(sandbox.State, id.ToString("N") + ".journal");
        var lines = await File.ReadAllLinesAsync(journal);
        var completion = Array.FindIndex(lines, line => JsonNode.Parse(line)!["Completed"]!.GetValue<bool>());
        Require(completion > 0, "A completed entry was not recorded.");
        await File.WriteAllTextAsync(journal, string.Join('\n', lines.Take(completion)) + "\n");
        using var recovered = new TransferManager(sandbox.State);
        Equal(TransferStatus.Paused, GetJob(recovered, id).Status);
        await recovered.RunAsync(id); Equal(TransferStatus.Completed, GetJob(recovered, id).Status, GetJob(recovered, id).Error);
        Equal(expected, await Hash(Path.Combine(sandbox.Destination, "intent.txt")));
        Equal(operation == TransferOperation.Copy, File.Exists(source));
    }
}

static async Task QueuedSpace()
{
    using var sandbox = new Sandbox();
    var source = Path.Combine(sandbox.Source, "queued.bin"); await WriteData(source, 2 * 1024 * 1024, 3);
    using var manager = new TransferManager(sandbox.State) { ReadSpace = _ => new StorageSpace(8L * 1024 * 1024, 4096, "NTFS") };
    await manager.CreateJobAsync([source], sandbox.Destination);
    try { await manager.CreateJobAsync([source], sandbox.Destination); throw new InvalidOperationException("Two queued transfers overbooked destination space."); }
    catch (TransferPreflightException error) { Require(error.Report.ReservedByQueuedJobsBytes >= 6L * 1024 * 1024, "The first job's allocation was not reserved."); }
    Equal(1, manager.Jobs.Count);
}

static async Task ParallelAbruptRecovery()
{
    using var sandbox = new Sandbox();
    for (int index = 0; index < 70; index++) await WriteData(Path.Combine(sandbox.Source, $"crash-{index:D3}.bin"), 1024 * 1024, index);
    Guid id;
    using (var seed = new TransferManager(sandbox.State)) id = (await seed.CreateJobAsync([sandbox.Source], sandbox.Destination,
        options: new TransferOptions { SmallFileWorkers = 4 })).Id;
    var signal = Path.Combine(sandbox.State, "parallel-worker-ready");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Sandbox).Assembly.Location);
    foreach (var argument in new[] { "--crash-worker", sandbox.State, signal }) start.ArgumentList.Add(argument);
    using (var worker = Process.Start(start) ?? throw new IOException("Recovery worker did not start."))
    {
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(signal) && !worker.HasExited && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
            Require(File.Exists(signal), "Parallel worker did not reach its kill point.");
        }
        finally { if (!worker.HasExited) worker.Kill(true); await worker.WaitForExitAsync(); }
    }
    using var recovered = new TransferManager(sandbox.State);
    Equal(TransferStatus.Paused, GetJob(recovered, id).Status);
    await recovered.RunAsync(id); Equal(TransferStatus.Completed, GetJob(recovered, id).Status, GetJob(recovered, id).Error);
    foreach (var source in Directory.EnumerateFiles(sandbox.Source)) await EqualFiles(source, Path.Combine(sandbox.Destination, "source", Path.GetFileName(source)));
}

static async Task ThrowsValidation(Func<Task<TransferJobSnapshot>> action)
{
    try { await action(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { return; }
    throw new InvalidOperationException("Unsafe source/destination relation was accepted.");
}

static async Task WriteData(string path, int length, int seed)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var data = new byte[1024 * 1024];
    new Random(seed).NextBytes(data);
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, data.Length, true);
    while (length > 0)
    {
        var count = Math.Min(length, data.Length);
        await stream.WriteAsync(data.AsMemory(0, count));
        length -= count;
    }
}

static async Task<string> Hash(string path)
{
    await using var input = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(input));
}

static async Task EqualFiles(string source, string destination) => Equal(await Hash(source), await Hash(destination), destination);

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string? context = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, actual {actual}. {context}");
}

sealed class Sandbox : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Stransfer-tests", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(root, "source");
    public string Destination => Path.Combine(root, "destination");
    public string State => Path.Combine(root, "state");

    public Sandbox()
    {
        System.IO.Directory.CreateDirectory(Source);
        System.IO.Directory.CreateDirectory(Destination);
    }

    public string Directory(params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        // Only this test's unique temporary directory is eligible for cleanup.
        try { System.IO.Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static class NativeSparse
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    internal static extern bool SetSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, IntPtr input, int inputLength, IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
}
