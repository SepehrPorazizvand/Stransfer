using System.Collections.Concurrent;
using Stransfer.Core;

namespace Stransfer.App.Services;

public sealed class TransferCoordinator : IDisposable
{
    public TransferManager Manager { get; }
    private readonly NamespaceTransferManager devices;
    private readonly ConcurrentDictionary<Guid, TransferJobSnapshot> snapshots = new();
    private readonly Dictionary<Guid, (CancellationTokenSource Stop, Task Task)> scheduled = new();
    private readonly SemaphoreSlim lane = new(1);
    private bool stopping;
    public IReadOnlyList<TransferJobSnapshot> Jobs => snapshots.Values.OrderByDescending(job => job.CreatedAt).ToArray();
    public event EventHandler<TransferJobSnapshot>? Finished;
    public TransferCoordinator(string directory)
    {
        Manager = new(directory);
        devices = new(Path.Combine(directory, "devices"));
        foreach (var job in Manager.Jobs) snapshots[job.Id] = job;
        foreach (var job in devices.Jobs) snapshots[job.Id] = job;
        Manager.JobChanged += (_, job) => snapshots[job.Id] = job;
        devices.JobChanged += (_, job) => snapshots[job.Id] = job;
        devices.OtherReservations = (id, destination) => Jobs.Where(job => job.Id != id && job.Status != TransferStatus.Completed
            && SameStorage(destination, job.DestinationDirectory)).Sum(job => Math.Max(0, job.TotalBytes - job.BytesTransferred));
        Manager.ReadSpace = path =>
        {
            var space = StorageProbe.Read(path);
            var reserved = devices.Jobs.Where(job => job.Status != TransferStatus.Completed && SameStorage(path, job.DestinationDirectory))
                .Sum(job => Math.Max(0, job.TotalBytes - job.BytesTransferred));
            return space with { AvailableBytes = Math.Max(0, space.AvailableBytes - reserved) };
        };
    }
    private static bool SameStorage(string a, string b) => TransferStorage.IsShell(a) || TransferStorage.IsShell(b)
        ? TransferStorage.IsShell(a) && TransferStorage.IsShell(b) : VolumeIdentity.Root(a).Equals(VolumeIdentity.Root(b), StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> RecoveryWarnings => Manager.RecoveryWarnings.Concat(devices.RecoveryWarnings).ToArray();
    public Task<TransferJobSnapshot> CreateAsync(string[] sources, string destination, TransferOperation operation, bool verify, TransferOptions options) =>
        sources.Any(TransferStorage.IsShell) || TransferStorage.IsShell(destination)
            ? devices.CreateAsync(sources, destination, operation, verify)
            : Task.Run(() => Manager.CreateJobAsync(sources, destination, operation, verify, options: options));
    public Task<bool> AreDrivesAvailableAsync(Guid id) => devices.Contains(id) ? devices.AvailableAsync(id) : Task.Run(() => Manager.AreDrivesAvailable(id));
    public bool IsScheduled(Guid id) => scheduled.ContainsKey(id);
    public void Schedule(Guid id)
    {
        if (stopping || scheduled.ContainsKey(id)) return;
        var stop = new CancellationTokenSource();
        var task = RunQueuedAsync(id, stop.Token);
        scheduled.Add(id, (stop, task));
        _ = FinishAsync(id, task, stop);
    }
    private async Task RunQueuedAsync(Guid id, CancellationToken token)
    {
        // Yield ensures this task is registered before even an empty transfer finishes.
        await Task.Yield();
        await lane.WaitAsync(token);
        try { if (devices.Contains(id)) await devices.RunAsync(id, token); else await Task.Run(() => Manager.RunAsync(id, token)); }
        finally { lane.Release(); }
    }
    private async Task FinishAsync(Guid id, Task task, CancellationTokenSource stop)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (snapshots.TryGetValue(id, out var job)) snapshots[id] = job with { Status = TransferStatus.Failed, Error = error.Message };
        }
        finally { scheduled.Remove(id); stop.Dispose(); }
        if (snapshots.TryGetValue(id, out var finished)) Finished?.Invoke(this, finished);
    }
    public void Pause(Guid id)
    {
        if (scheduled.TryGetValue(id, out var pending)) pending.Stop.Cancel();
        if (!devices.Contains(id)) Manager.Pause(id);
    }
    public async Task RemoveAsync(Guid id)
    {
        if (IsScheduled(id)) throw new InvalidOperationException("Pause the transfer before removing it.");
        // Completed history has no partial cleanup and can be removed with the original drives absent.
        bool deletePartials = !snapshots.TryGetValue(id, out var job) || job.Status != TransferStatus.Completed;
        if (devices.Contains(id)) await devices.RemoveAsync(id);
        else await Task.Run(() => Manager.RemoveAsync(id, deletePartialFiles: deletePartials));
        snapshots.TryRemove(id, out _);
    }
    public async Task StopAsync()
    {
        stopping = true;
        foreach (var item in scheduled.ToArray()) { item.Value.Stop.Cancel(); if (!devices.Contains(item.Key)) Manager.Pause(item.Key); }
        try { await Task.WhenAll(scheduled.Values.Select(item => item.Task).ToArray()); } catch (OperationCanceledException) { }
    }
    public void Dispose() { Manager.Dispose(); devices.Dispose(); lane.Dispose(); }
}
