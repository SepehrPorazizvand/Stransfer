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

[ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOperationFlags(uint flags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object dialog);
    [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
    [PreserveSig] int SetOwnerWindow(IntPtr owner);
    [PreserveSig] int ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
    [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
    [PreserveSig] int RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
    [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
    [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.Interface)] object destination);
    [PreserveSig] int CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
    [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
    [PreserveSig] int DeleteItem([MarshalAs(UnmanagedType.Interface)] object item, IFileOperationProgressSink? sink);
    [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
    [PreserveSig] int NewItem([MarshalAs(UnmanagedType.Interface)] object destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
public interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object? newItem);
    [PreserveSig] int PreMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
    [PreserveSig] int PostMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, [MarshalAs(UnmanagedType.Interface)] object? newItem);
    [PreserveSig] int PreCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
    [PreserveSig] int PostCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, [MarshalAs(UnmanagedType.Interface)] object? newItem);
    [PreserveSig] int PreDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item);
    [PreserveSig] int PostDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, int result, [MarshalAs(UnmanagedType.Interface)] object? newItem);
    [PreserveSig] int PreNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, [MarshalAs(UnmanagedType.Interface)] object? newItem);
    [PreserveSig] int UpdateProgress(uint total, uint completed);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellCopyProgress(IProgress<int>? progress, CancellationToken token) : IFileOperationProgressSink
{
    public int Error { get; private set; }
    private int Check() => token.IsCancellationRequested ? unchecked((int)0x80004004) : 0;
    private int Record(int result) { if (result < 0 && Error == 0) Error = result; return 0; }
    public int StartOperations() => Check();
    public int FinishOperations(int result) => Record(result);
    public int PreRenameItem(uint flags, object item, string name) => Check();
    public int PostRenameItem(uint flags, object item, string name, int result, object? newItem) => Record(result);
    public int PreMoveItem(uint flags, object item, object destination, string? name) => Check();
    public int PostMoveItem(uint flags, object item, object destination, string? name, int result, object? newItem) => Record(result);
    public int PreCopyItem(uint flags, object item, object destination, string? name) => Check();
    public int PostCopyItem(uint flags, object item, object destination, string? name, int result, object? newItem) => Record(result);
    public int PreDeleteItem(uint flags, object item) => Check();
    public int PostDeleteItem(uint flags, object item, int result, object? newItem) => Record(result);
    public int PreNewItem(uint flags, object destination, string name) => Check();
    public int PostNewItem(uint flags, object destination, string name, string? template, uint attributes, int result, object? newItem) => Record(result);
    public int UpdateProgress(uint total, uint completed) { if (total > 0) progress?.Report((int)Math.Min(100UL, (ulong)completed * 100 / total)); return Check(); }
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
