using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Stransfer.App.Services;

[ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemProperties
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid id, out IntPtr result);
    void GetParent(out IShellItem parent); void GetDisplayName(uint kind, out IntPtr name);
    void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
    void GetPropertyStore(uint flags, ref Guid id, out IntPtr value);
}

// Vtable order follows the Windows SDK ShObjIdl_core.h. No shell copy operation is used.
[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid id, out IntPtr value);
    void GetPropertyStore(uint flags, ref Guid id, out IntPtr value);
    void GetPropertyDescriptionList(IntPtr key, ref Guid id, out IntPtr value);
    void GetAttributes(uint flags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out IEnumShellItems items);
}

[ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
    void Skip(uint count); void Reset(); void Clone(out IEnumShellItems items);
}

[ComImport, Guid("00adb003-bde9-45c6-8e29-d09f9353e108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITransferSource
{
    void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie); void SetProperties(IntPtr properties);
    void OpenItem(IShellItem item, uint flags, ref Guid id, out IntPtr value);
    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem result);
    void RecycleItem(IShellItem item, IShellItem destination, uint flags, out IShellItem result);
    void RemoveItem(IShellItem item, uint flags);
    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem result);
}

[ComImport, Guid("000214e6-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INamespaceFolder
{
    void ParseDisplayName(IntPtr window, IntPtr context, IntPtr name, IntPtr eaten, out IntPtr idList, IntPtr attributes);
    void EnumObjects(IntPtr window, uint flags, out IntPtr items);
    void BindToObject(IntPtr idList, IntPtr context, ref Guid id, out IntPtr value);
    void BindToStorage(IntPtr idList, IntPtr context, ref Guid id, out IntPtr value);
    void CompareIDs(IntPtr hint, IntPtr first, IntPtr second);
    void CreateViewObject(IntPtr window, ref Guid id, out IntPtr value);
    void GetAttributesOf(uint count, IntPtr items, ref uint attributes);
    void GetUIObjectOf(IntPtr window, uint count, IntPtr items, ref Guid id, IntPtr reserved, out IntPtr value);
    void GetDisplayNameOf(IntPtr idList, uint flags, IntPtr name);
    void SetNameOf(IntPtr window, IntPtr idList, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newIdList);
}

[ComImport, Guid("48addd32-3ca5-4124-abe3-b5a72531b207"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITransferDestination
{
    void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie);
    void CreateItem([MarshalAs(UnmanagedType.LPWStr)] string name, uint attributes, ulong size, uint flags,
        ref Guid itemId, out IShellItem item, ref Guid resourcesId, out IShellItemResources resources);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ShellResource
{
    public Guid Type;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
}

[ComImport, Guid("2dd81fe3-a83c-4da9-a330-47249d345ba1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumResources
{
    [PreserveSig] int Next(uint count, out ShellResource resource, out uint fetched);
    void Skip(uint count); void Reset(); void Clone(out IEnumResources resources);
}

[ComImport, Guid("ff5693be-2ce0-4d48-b5c5-40817d1acdb9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemResources
{
    void GetAttributes(out uint attributes); void GetSize(out ulong size);
    void GetTimes(out FILETIME creation, out FILETIME write, out FILETIME access);
    void SetTimes(IntPtr creation, IntPtr write, IntPtr access);
    void GetResourceDescription(ref ShellResource resource, out IntPtr description);
    void EnumResources(out IEnumResources resources);
    [PreserveSig] int SupportsResource(ref ShellResource resource);
    void OpenResource(ref ShellResource resource, ref Guid id, out IntPtr value);
    void CreateResource(ref ShellResource resource, ref Guid id, out IntPtr value);
    void MarkForDelete();
}

internal static class NamespaceCom
{
    internal static Guid Transfer = new("d5e346a1-f753-4932-b403-4574800e2498");
    internal static Guid Stream = new("1cebb3ab-7c10-499a-a417-92ca16c4cb83");
    internal static Guid Enumeration = new("94f60519-2850-4924-aa5a-d15e84868039");
    internal static string Name(IShellItem item, uint kind)
    {
        Marshal.ThrowExceptionForHR(item.GetDisplayName(kind, out var pointer));
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("The device returned an empty item name."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    internal static T Bind<T>(IShellItem item, Guid handler)
    {
        var id = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref handler, ref id, out var pointer));
        try { return (T)Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }
    internal static IShellItem Open(string name)
    {
        var id = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(name, IntPtr.Zero, ref id, out var item));
        return item;
    }
    internal static IStream WritableStream(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var context));
        IntPtr pointer = IntPtr.Zero;
        try
        {
            var options = new BIND_OPTS { cbStruct = Marshal.SizeOf<BIND_OPTS>(), grfMode = 1 | 0x20 };
            context.SetBindOptions(ref options);
            pointer = Marshal.GetComInterfaceForObject<IBindCtx, IBindCtx>(context);
            var handler = Stream; var id = typeof(IStream).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(pointer, ref handler, ref id, out var value));
            try { return (IStream)Marshal.GetObjectForIUnknown(value); } finally { Marshal.Release(value); }
        }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); Release(context); }
    }
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    internal static Task<T> Sta<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            bool initialized = false;
            try { Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero)); initialized = true; result.TrySetResult(work()); }
            catch (OperationCanceledException) { result.TrySetCanceled(); }
            catch (Exception error) { result.TrySetException(error); }
            finally { if (initialized) OleUninitialize(); }
        }) { IsBackground = true, Name = "Stransfer namespace engine" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid id, out IShellItem item);
    [DllImport("shell32.dll")] internal static extern int SHCreateShellItemArrayFromDataObject(IDataObject data, ref Guid id, out IShellItemArray items);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(uint reserved, out IBindCtx context);
}
