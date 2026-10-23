using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stransfer.Core;

public sealed partial class TransferManager
{
    private void Save(JobState state, FilePlan? file = null, bool rewriteManifest = false, bool durable = true)
    {
        lock (state.Sync)
        {
            state.UpdatedAt = DateTimeOffset.UtcNow;
            if (state.ManifestSaved && !rewriteManifest)
            {
                var entry = new ProgressEntry
                {
                    Epoch = state.JournalEpoch, Status = state.Status, Error = state.Error, UpdatedAt = state.UpdatedAt,
                    FileIndex = file?.Index, DurableOffset = file?.DurableOffset ?? 0,
                    ReadyToCommit = file?.ReadyToCommit ?? false, DestinationCommitted = file?.DestinationCommitted ?? false,
                    Completed = file?.Completed ?? false, AtomicMove = file?.AtomicMove ?? false,
                    VerifiedHash = file?.VerifiedHash, SourceFileIdentity = file?.SourceFileIdentity
                };
                var data = JsonSerializer.SerializeToUtf8Bytes(entry);
                long journalLength;
                using (var journal = new FileStream(JournalPath(state), FileMode.Append, FileAccess.Write, FileShare.Read, 4096))
                {
                    journal.Write(data);
                    journal.WriteByte((byte)'\n');
                    journal.Flush(flushToDisk: durable);
                    journalLength = journal.Length;
                }
                // The threshold scales with the immutable plan, making compaction cost
                // amortized linear even for hundreds of thousands of tiny files.
                if (journalLength < Math.Max(4L * 1024 * 1024, state.ManifestLength * 2)) return;
            }
            state.JournalEpoch = Guid.NewGuid();
            var path = StatePath(state);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            state.ManifestSaved = true;
            state.ManifestLength = new FileInfo(path).Length;
            // Epochs make an old journal harmless if power fails between the atomic
            // manifest replacement and clearing the old journal.
            using var cleared = new FileStream(JournalPath(state), FileMode.Create, FileAccess.Write, FileShare.Read, 4096);
            cleared.Flush(flushToDisk: true);
        }
    }

    private string StatePath(JobState state) => Path.Combine(_stateDirectory, state.Id.ToString("N") + ".json");
    private string JournalPath(JobState state) => Path.Combine(_stateDirectory, state.Id.ToString("N") + ".journal");

    private void ReplayJournal(JobState state)
    {
        var path = JournalPath(state);
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        var start = 0;
        var validLength = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ProgressEntry>(bytes.AsSpan(start, index - start));
                if (entry is null) throw new InvalidDataException("Invalid progress entry.");
                if (entry.Epoch == state.JournalEpoch)
                {
                    if (!Enum.IsDefined(entry.Status)) throw new InvalidDataException("Invalid progress status.");
                    if (entry.FileIndex is { } fileIndex)
                    {
                        if (fileIndex < 0 || fileIndex >= state.Files.Count) throw new InvalidDataException("Invalid progress file index.");
                        var file = state.Files[fileIndex];
                        if (entry.DurableOffset < 0 || entry.DurableOffset > file.Length) throw new InvalidDataException("Invalid progress offset.");
                        file.DurableOffset = entry.DurableOffset;
                        file.ReadyToCommit = entry.ReadyToCommit; file.DestinationCommitted = entry.DestinationCommitted;
                        file.Completed = entry.Completed; file.AtomicMove = entry.AtomicMove;
                        file.VerifiedHash = entry.VerifiedHash; file.SourceFileIdentity = entry.SourceFileIdentity;
                    }
                    state.Status = entry.Status; state.Error = entry.Error; state.UpdatedAt = entry.UpdatedAt;
                }
                validLength = index + 1;
                start = index + 1;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _recoveryWarnings.Add($"An incomplete progress journal was recovered for {state.Id:N}: {ex.Message}");
                break;
            }
        }
        if (validLength != bytes.Length)
        {
            using var journal = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            journal.SetLength(validLength);
            journal.Flush(flushToDisk: true);
        }
    }

}
