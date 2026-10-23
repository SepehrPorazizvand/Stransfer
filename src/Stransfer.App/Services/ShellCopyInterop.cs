using System.Runtime.InteropServices;

namespace Stransfer.App.Services;

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid id, out IntPtr result);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}

