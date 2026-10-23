using System.Text.Json;

namespace Stransfer.Core;

public sealed partial class TransferManager
{
    // Each waiter proceeds only after its own intent is flushed. Parallel small files share one disk barrier.
    private Task SaveFileAsync(JobState state, FilePlan file)
    {
        if (state.EffectiveWorkers <= 1) { Save(state, file); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (state.Sync)
        {
            state.PendingJournal.Enqueue(new(new ProgressEntry
            {
                Status = state.Status, Error = state.Error, UpdatedAt = DateTimeOffset.UtcNow,
                FileIndex = file.Index, DurableOffset = file.DurableOffset, ReadyToCommit = file.ReadyToCommit,
                DestinationCommitted = file.DestinationCommitted, Completed = file.Completed,
                AtomicMove = file.AtomicMove, VerifiedHash = file.VerifiedHash, SourceFileIdentity = file.SourceFileIdentity
            }, completion));
            if (!state.JournalWriterRunning)
            {
                state.JournalWriterRunning = true;
                state.JournalBatchReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = FlushBatchAsync(state);
            }
            if (state.PendingJournal.Count >= state.EffectiveWorkers) state.JournalBatchReady?.TrySetResult();
        }
        return completion.Task;
    }

    private async Task FlushBatchAsync(JobState state)
    {
        while (true)
        {
            // A short coalescing window amortizes journal flushes across small-file workers.
            // Flush as soon as all active workers have contributed. A timer-only batching
            // window can round to a Windows timer tick for every group of tiny files.
            Task ready;
            lock (state.Sync) ready = state.JournalBatchReady!.Task;
            await Task.WhenAny(ready, Task.Delay(2)).ConfigureAwait(false);
            lock (state.Sync)
            {
                var batch = state.PendingJournal.ToArray();
                state.PendingJournal.Clear();
                try
                {
                    using (var journal = new FileStream(JournalPath(state), FileMode.Append, FileAccess.Write, FileShare.Read, 65536))
                    {
                        foreach (var pending in batch)
                        {
                            pending.Entry.Epoch = state.JournalEpoch;
                            journal.Write(JsonSerializer.SerializeToUtf8Bytes(pending.Entry));
                            journal.WriteByte((byte)'\n');
                        }
                        journal.Flush(flushToDisk: true);
                        state.UpdatedAt = DateTimeOffset.UtcNow;
                        if (journal.Length >= Math.Max(4L * 1024 * 1024, state.ManifestLength * 2))
                        {
                            journal.Dispose();
                            Save(state, rewriteManifest: true);
                        }
                    }
                    foreach (var pending in batch) pending.Completion.TrySetResult();
                }
                catch (Exception error)
                { foreach (var pending in batch) pending.Completion.TrySetException(error); }
                if (state.PendingJournal.Count == 0) { state.JournalWriterRunning = false; state.JournalBatchReady = null; return; }
                state.JournalBatchReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (state.PendingJournal.Count >= state.EffectiveWorkers) state.JournalBatchReady.TrySetResult();
            }
        }
    }
}
