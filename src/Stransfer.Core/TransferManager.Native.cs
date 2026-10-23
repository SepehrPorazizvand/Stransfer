using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Stransfer.Core;

public sealed partial class TransferManager
{
    // Uses Windows' optimized data path, with Stransfer's queue, checkpoints and final publish.
    // CopyFileEx has no Explorer progress dialog. Resuming its stopped partial uses our verified-prefix pipeline.
    private void NativeCopy(JobState state, FilePlan file, CancellationToken token)
    {
        using var sourceLock = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        ValidateSource(file);
        Exception? failure = null;
        long checkpoint = 0;
        bool firstProgress = true;
        var timer = Stopwatch.StartNew();
        var display = Stopwatch.StartNew();
        CopyProgress callback = (total, transferred, streamSize, streamTransferred, streamNumber, reason, source, destination, data) =>
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    FlushPause(destination);
                    return 2; // PROGRESS_STOP retains our unique partial file.
                }
                if (streamNumber != 1) return 0;
                SetOffset(state, file, Math.Clamp(transferred, 0, file.Length));
                // Video/photo folders often contain hundreds of 32–256 MiB files. An initial
                // 32 MiB barrier in every file drains the disk cache twice per file. Keep the
                // timed/256 MiB checkpoint for long copies and always flush before publishing.
                if (file.Offset < file.Length && (file.Offset - checkpoint >= NativeCheckpointBytes || timer.ElapsedMilliseconds >= 1000))
                {
                    if (!FlushFileBuffers(destination)) throw new IOException("The native copy checkpoint could not be flushed.", new Win32Exception(Marshal.GetLastWin32Error()));
                    lock (state.Sync) file.DurableOffset = file.Offset;
                    Save(state, file); checkpoint = file.Offset; timer.Restart(); Publish(state); display.Restart();
                }
                else if (file.Offset > 0 && firstProgress || display.ElapsedMilliseconds >= 150)
                { firstProgress = false; Publish(state); display.Restart(); }
                if (token.IsCancellationRequested) { FlushPause(destination); return 2; }
                return 0;
            }
            catch (Exception error) { failure = error; return 2; }
        };
        int cancel = 0;
        // Bypass the system cache for large files; otherwise each durable checkpoint drains
        // buffered video data and competes with the following file's read-ahead.
        uint flags = 1u | (file.Length >= NativeUnbufferedThreshold ? 0x1000u : 0u); // FAIL_IF_EXISTS | NO_BUFFERING
        var success = CopyFileEx(file.Source, file.Partial, callback, IntPtr.Zero, ref cancel, flags);
        var errorCode = Marshal.GetLastWin32Error(); GC.KeepAlive(callback);
        if (failure is not null) throw new IOException("The native copy stopped while saving progress.", failure);
        if (File.Exists(file.Partial))
        {
            // CopyFileEx preserves read-only attributes. Keep our private partial writable
            // for flushing/resume; ApplyMetadata restores the source attributes at publish.
            var attributes = File.GetAttributes(file.Partial);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file.Partial, attributes & ~FileAttributes.ReadOnly);
        }
        if (token.IsCancellationRequested && File.Exists(file.Partial))
        {
            // An intentional pause saves the last complete native block even before the
            // periodic checkpoint. A longer interval must not discard pause progress.
            using var paused = new FileStream(file.Partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
            paused.Flush(true);
            lock (state.Sync) file.DurableOffset = file.Offset;
            Save(state, file);
        }
        token.ThrowIfCancellationRequested();
        if (!success) throw new IOException("The native copy could not complete.", new Win32Exception(errorCode));
        using var output = new FileStream(file.Partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
        if (output.Length != file.Length) throw new IOException("The native copy length does not match its source.");
        output.Flush(true); ValidateSource(file);
        SetOffset(state, file, file.Length);
        lock (state.Sync) { file.DurableOffset = file.Length; file.ReadyToCommit = true; }
        Save(state, file);
        void FlushPause(IntPtr destination)
        {
            if (destination == IntPtr.Zero || destination == new IntPtr(-1)) return;
            if (!FlushFileBuffers(destination)) throw new IOException("Pause progress could not be flushed.", new Win32Exception(Marshal.GetLastWin32Error()));
            lock (state.Sync) file.DurableOffset = file.Offset;
            Save(state, file);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CopyProgress(long total, long transferred, long streamSize, long streamTransferred, uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CopyFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string source, string destination, CopyProgress progress, IntPtr data, ref int cancel, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr file);
}
