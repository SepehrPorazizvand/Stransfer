using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Stransfer.App.Services;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Stransfer.App.SmokeTests;

internal static class ShellTransferTests
{
    public static async Task RunAsync(string root)
    {
        var source = Path.Combine(root, "shell-source", "device-folder");
        var destination = Path.Combine(root, "shell-copy");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "device-file.txt"), "Namespace object contents.");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var itemId = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source, IntPtr.Zero, ref itemId, out var item));
        ComDataObject? data = null;
        try
        {
            var handler = new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4"); // BHID_DataObject
            var dataId = typeof(ComDataObject).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref handler, ref dataId, out var pointer));
            try { data = (ComDataObject)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            var selection = new NamespaceSelection(data);
            Require(ShellClipboardReader.HasShellItems(selection), "Shell IDList device selection was not recognized.");
            using (var transfer = new ShellClipboardTransfer(selection))
                await transfer.CopyAsync(destination, null, CancellationToken.None, showProgress: false);
            Require(File.ReadAllText(Path.Combine(destination, "device-folder", "device-file.txt")) == "Namespace object contents."
                && Directory.Exists(Path.Combine(destination, "device-folder", "empty")), "Namespace folder copy lost data or empty directories.");
            using (var transfer = new ShellClipboardTransfer(selection))
                await transfer.CopyAsync(destination, null, CancellationToken.None, showProgress: false);
            Require(Directory.GetDirectories(destination).Length == 2 && Directory.Exists(source), "Device keep-both copy overwrote a destination or removed its source.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var stopped = new ShellClipboardTransfer(selection);
            try { await stopped.CopyAsync(destination, null, cancelled.Token, showProgress: false); throw new InvalidOperationException("Cancelled device copy was accepted."); }
            catch (OperationCanceledException) { }
            var sink = new ShellCopyProgress(null, cancelled.Token);
            Require(sink.UpdateProgress(100, 20) < 0, "Exit cancellation was not sent to the shell provider.");
            Console.WriteLine("PASS  Shell IDList selection without FileDrop, native STA folder copy, contents, empty directories, keep-both collision, source retention and cancellation");
        }
        finally { if (data is not null) Marshal.ReleaseComObject(data); Marshal.ReleaseComObject(item); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid id, out IShellItem item);
}

// Exposes the namespace format used by portable devices, with ordinary filesystem paths deliberately unavailable.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NamespaceSelection(ComDataObject inner) : ComDataObject
{
    public int QueryGetData(ref FORMATETC format) => format.cfFormat == 15 ? unchecked((int)0x80040064) : inner.QueryGetData(ref format);
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (format.cfFormat == 15) throw new COMException("FileDrop unavailable", unchecked((int)0x80040064));
        inner.GetData(ref format, out medium);
    }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => inner.GetDataHere(ref format, ref medium);
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) => inner.GetCanonicalFormatEtc(ref input, out output);
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => inner.SetData(ref format, ref medium, release);
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) => inner.DAdvise(ref format, flags, sink, out connection);
    public void DUnadvise(int connection) => inner.DUnadvise(connection);
    public int EnumDAdvise(out IEnumSTATDATA enumerator) { var result = inner.EnumDAdvise(out var value); enumerator = value!; return result; }
}
