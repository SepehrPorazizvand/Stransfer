using System.Runtime.InteropServices;
using System.Text;

namespace Stransfer.App.Services;

public sealed record ExplorerTarget(IntPtr Window, IntPtr Focus, string Directory);

internal static class ExplorerFolderResolver
{
    public static ExplorerTarget? Read()
    {
        var foreground = GetForegroundWindow();
        var name = new StringBuilder(128); GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is not ("CabinetWClass" or "ExploreWClass")) return null;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero) return null;
        // Ctrl+Shift+V is an explicit file-transfer command. Toolbar/address-bar focus
        // can still identify the active folder; Ctrl+V is never handled here.
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            int count = ((dynamic)windows).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null, browserObject = null, folderObject = null;
                IShellView? view = null;
                try
                {
                    item = ((dynamic)windows).Item(index);
                    if (item is null || (long)((dynamic)item).HWND != foreground.ToInt64()) continue;
                    var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
                    var browserId = typeof(IShellBrowser).GUID;
                    if (((IComServiceProvider)item).QueryService(ref service, ref browserId, out browserObject) != 0) continue;
                    if (((IShellBrowser)browserObject).QueryActiveShellView(out view) != 0 || view is null) continue;
                    if (view.GetWindow(out var viewWindow) != 0 || !IsWindowVisible(viewWindow)) continue;
                    // Resolve the focused view itself. This avoids picking an inactive Explorer tab's path.
                    var folderId = typeof(IPersistFolder2).GUID;
                    ((IFolderView)view).GetFolder(ref folderId, out folderObject);
                    ((IPersistFolder2)folderObject).GetCurFolder(out var pidl);
                    try
                    {
                        var path = new StringBuilder(32768);
                        if (SHGetPathFromIDListEx(pidl, path, (uint)path.Capacity, 0) && Path.IsPathFullyQualified(path.ToString()))
                            candidates.Add(path.ToString());
                    }
                    finally { Marshal.FreeCoTaskMem(pidl); }
                }
                catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(folderObject); Release(view); Release(browserObject); Release(item); }
            }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(windows); Release(shell); }
        return candidates.Count == 1 ? new(foreground, info.Focus, candidates.Single()) : null;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public static bool StillFocused(ExplorerTarget target)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetForegroundWindow() == target.Window && GetGUIThreadInfo(0, ref info) && info.Focus == target.Focus;
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size; public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDListEx(IntPtr pidl, StringBuilder path, uint count, uint flags);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object result); }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        void ContextSensitiveHelp(bool enter); void InsertMenusSB(IntPtr menu, IntPtr widths); void SetMenuSB(IntPtr menu, IntPtr reserved, IntPtr active);
        void RemoveMenusSB(IntPtr menu); void SetStatusTextSB(IntPtr text); void EnableModelessSB(bool enable);
        void TranslateAcceleratorSB(IntPtr message, ushort id); void BrowseObject(IntPtr pidl, uint flags); void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr window); void SendControlMsg(uint id, uint message, IntPtr w, IntPtr l, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView { [PreserveSig] int GetWindow(out IntPtr window); }
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode(out uint mode); void SetCurrentViewMode(uint mode);
        void GetFolder(ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object folder);
    }
    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2 { void GetClassID(out Guid id); void Initialize(IntPtr pidl); void GetCurFolder(out IntPtr pidl); }
}
