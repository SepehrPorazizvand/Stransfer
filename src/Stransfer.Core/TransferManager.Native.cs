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
        var timer = Stopwatch.StartNew();
        var display = Stopwatch.StartNew();
        CopyProgress callback = (total, transferred, streamSize, streamTransferred, streamNumber, reason, source, destination, data) =>
        {
            try
            {
                if (token.IsCancellationRequested) return 2; // PROGRESS_STOP retains our unique partial file.
                if (streamNumber != 1) return 0;
                SetOffset(state, file, Math.Clamp(transferred, 0, file.Length));
                if (file.Offset - checkpoint >= (checkpoint == 0 ? 32L : 256L) * 1024 * 1024 || timer.ElapsedMilliseconds >= 1000)
                {
                    if (!FlushFileBuffers(destination)) throw new IOException("The native copy checkpoint could not be flushed.", new Win32Exception(Marshal.GetLastWin32Error()));
                    lock (state.Sync) file.DurableOffset = file.Offset;
                    Save(state, file); checkpoint = file.Offset; timer.Restart(); Publish(state); display.Restart();
                }
                else if (display.ElapsedMilliseconds >= 150) { Publish(state); display.Restart(); }
                return token.IsCancellationRequested ? 2u : 0u;
            }
            catch (Exception error) { failure = error; return 2; }
        };
        int cancel = 0;
        var success = CopyFileEx(file.Source, file.Partial, callback, IntPtr.Zero, ref cancel, 1); // Fail if another file owns this partial path.
        var errorCode = Marshal.GetLastWin32Error(); GC.KeepAlive(callback);
        if (failure is not null) throw new IOException("The native copy stopped while saving progress.", failure);
        token.ThrowIfCancellationRequested();
        if (!success) throw new IOException("The native copy could not complete.", new Win32Exception(errorCode));
        using var output = new FileStream(file.Partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
        if (output.Length != file.Length) throw new IOException("The native copy length does not match its source.");
        output.Flush(true); ValidateSource(file);
        SetOffset(state, file, file.Length);
        lock (state.Sync) { file.DurableOffset = file.Length; file.ReadyToCommit = true; }
        Save(state, file);
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
