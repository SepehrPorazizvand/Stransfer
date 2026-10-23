using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Stransfer.Core;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Stransfer.App.Services;

/// <summary>Reads shell selections such as MTP phones without staging their contents on the PC.</summary>
internal static class ShellClipboardReader
{
    public static ClipboardTransfer? Read()
    {
        var files = DesktopDialogs.ReadClipboard();
        if (files is not null && files.Sources.All(path => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path)))) return files;
        var sequence = ExplorerPasteService.ClipboardSequence;
        Marshal.ThrowExceptionForHR(OleGetClipboard(out var data));
        if (data is null) return null;
        var owned = false;
        try
        {
            if (sequence != ExplorerPasteService.ClipboardSequence || !HasShellItems(data)) return null;
            var result = new ClipboardTransfer([], TransferOperation.Copy, sequence) { Shell = new(data) };
            owned = true;
            return result;
        }
        finally { if (!owned && Marshal.IsComObject(data)) Marshal.ReleaseComObject(data); }
    }
    internal static bool HasIdList(ComDataObject data) => HasFormat(data, "Shell IDList Array");
    internal static bool HasShellItems(ComDataObject data) => HasIdList(data);
    private static bool HasFormat(ComDataObject data, string name)
    {
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat(name)),
            dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        return data.QueryGetData(ref format) == 0;
    }
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out ComDataObject? data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
}

/// <summary>Device copies use the shell's MTP provider and progress UI. They never delete the phone originals.</summary>
internal sealed class ShellClipboardTransfer(ComDataObject data) : IDisposable
{
    private ComDataObject? source = data;
    private bool started;
    public Task CopyAsync(string destination, IProgress<int>? progress, CancellationToken token, bool showProgress = true)
    {
        token.ThrowIfCancellationRequested();
        if (started || source is null) throw new InvalidOperationException("Copy the device files again before pasting.");
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Choose an existing PC destination folder.");
        var dataId = typeof(ComDataObject).GUID;
        Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref dataId, source, out var marshaled));
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ComDataObject? proxy = null;
            bool initialized = false, consumed = false;
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero)); initialized = true;
                consumed = true;
                Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(marshaled, ref dataId, out proxy));
                token.ThrowIfCancellationRequested();
                CopyOnSta(proxy, destination, progress, token, showProgress);
                result.TrySetResult();
            }
            catch (OperationCanceledException) { result.TrySetCanceled(); }
            catch (Exception error) { result.TrySetException(error); }
            finally
            {
                if (!consumed) ReleaseMarshaled(marshaled);
                if (proxy is not null && Marshal.IsComObject(proxy)) Marshal.ReleaseComObject(proxy);
                if (initialized) OleUninitialize();
            }
        }) { IsBackground = true, Name = "Stransfer device copy" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); started = true; }
        catch { ReleaseMarshaled(marshaled); throw; }
        return result.Task;
    }
    private static void CopyOnSta(ComDataObject data, string destination, IProgress<int>? progress, CancellationToken token, bool showProgress)
    {
        IShellItem? folder = null;
        IFileOperation? operation = null;
        uint cookie = 0;
        var sink = new ShellCopyProgress(progress, token);
        try
        {
            var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination, IntPtr.Zero, ref itemId, out folder));
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
            // Keep both on collision, create nested folders without an extra confirmation; show normal progress/errors.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x0008u | 0x0200u | (showProgress ? 0u : 0x0004u | 0x0010u | 0x0400u)));
            Marshal.ThrowExceptionForHR(operation.Advise(sink, out cookie));
            Marshal.ThrowExceptionForHR(operation.CopyItems(data, folder));
            int result = operation.PerformOperations();
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted || token.IsCancellationRequested) throw new OperationCanceledException("Device copy stopped.");
            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(sink.Error);
        }
        finally
        {
            if (cookie != 0) operation?.Unadvise(cookie);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            if (folder is not null && Marshal.IsComObject(folder)) Marshal.ReleaseComObject(folder);
            GC.KeepAlive(sink);
        }
    }
    public void Dispose()
    {
        if (source is { } value && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        source = null;
    }
    private static void ReleaseMarshaled(IntPtr stream) { CoReleaseMarshalData(stream); Marshal.Release(stream); }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid id, [MarshalAs(UnmanagedType.Interface)] ComDataObject data, out IntPtr stream);
    [DllImport("ole32.dll")] private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid id, out ComDataObject data);
    [DllImport("ole32.dll")] private static extern int CoReleaseMarshalData(IntPtr stream);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid id, out IShellItem item);
}
