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
            var result = new ClipboardTransfer([], ReadOperation(data), sequence) { Shell = new(data) };
            owned = true;
            return result;
        }
        finally { if (!owned && Marshal.IsComObject(data)) Marshal.ReleaseComObject(data); }
    }
    internal static bool HasIdList(ComDataObject data) => HasFormat(data, "Shell IDList Array");
    internal static bool HasShellItems(ComDataObject data) => HasIdList(data);
    private static TransferOperation ReadOperation(ComDataObject data)
    {
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat("Preferred DropEffect")),
            dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        if (data.QueryGetData(ref format) != 0) return TransferOperation.Copy;
        data.GetData(ref format, out var medium);
        try
        {
            var pointer = GlobalLock(medium.unionmember);
            if (pointer == IntPtr.Zero) return TransferOperation.Copy;
            try { return (Marshal.ReadInt32(pointer) & 2) != 0 ? TransferOperation.Move : TransferOperation.Copy; }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }
    private static bool HasFormat(ComDataObject data, string name)
    {
        var format = new FORMATETC { cfFormat = unchecked((short)RegisterClipboardFormat(name)),
            dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        return data.QueryGetData(ref format) == 0;
    }
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out ComDataObject? data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}

/// <summary>Converts selected Shell IDs to durable locators without Windows copy UI.</summary>
internal sealed class ShellClipboardTransfer(ComDataObject data) : IDisposable
{
    private ComDataObject? source = data;
    internal string[] ReadSources()
    {
        if (source is null) throw new ObjectDisposedException(nameof(ShellClipboardTransfer));
        var id = typeof(IShellItemArray).GUID;
        Marshal.ThrowExceptionForHR(NamespaceCom.SHCreateShellItemArrayFromDataObject(source, ref id, out var items));
        try
        {
            items.GetCount(out var count);
            var paths = new string[checked((int)count)];
            for (uint index = 0; index < count; index++)
            {
                items.GetItemAt(index, out var item);
                try { paths[index] = TransferStorage.Encode(item); }
                finally { NamespaceCom.Release(item); }
            }
            return paths;
        }
        finally { NamespaceCom.Release(items); }
    }
    public void Dispose() { NamespaceCom.Release(source); source = null; }
}
