using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Stransfer.App.Services;

/// <summary>Registers only Ctrl+Shift+V; ordinary Windows paste is never intercepted.</summary>
public sealed class ExplorerPasteService : IDisposable
{
    private readonly IShortcutPlatform platform;
    private readonly Func<ClipboardTransfer, string, Task> accept;
    private readonly Func<ClipboardTransfer, Task> prepare;
    private readonly Action<string> report;
    private readonly Action<bool, string> status;
    private readonly Func<bool> canAccept;
    private bool accepting;
    public bool Enabled { get; private set; }
    public static uint ClipboardSequence => GetClipboardSequenceNumber();

    public ExplorerPasteService(Window window, Func<ClipboardTransfer, string, Task> accept,
        Func<ClipboardTransfer, Task> prepare, Action<string> report, Action<bool, string> status, Func<bool> canAccept)
        : this(new WindowShortcutPlatform(window), accept, prepare, report, status, canAccept) { }

    internal ExplorerPasteService(IShortcutPlatform platform, Func<ClipboardTransfer, string, Task> accept,
        Func<ClipboardTransfer, Task> prepare, Action<string> report, Action<bool, string> status, Func<bool> canAccept)
    {
        this.platform = platform; this.accept = accept; this.prepare = prepare;
        this.report = report; this.status = status; this.canAccept = canAccept;
        platform.Pressed += OnPressed;
    }
    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            platform.Unregister(); Enabled = false;
            status(false, "Disabled. Enable to use Ctrl+Shift+V while Stransfer is running.");
            return;
        }
        if (Enabled) { status(true, "Ready · Ctrl+Shift+V is registered."); return; }
        Enabled = platform.TryRegister(out var error);
        status(Enabled, Enabled ? "Ready · Ctrl+Shift+V is registered."
            : error == 1409 ? "Unavailable · Another application uses Ctrl+Shift+V. Close it, then retry."
            : $"Unavailable · Windows could not register Ctrl+Shift+V (error {error}). Retry below.");
    }
    private async void OnPressed() => await HandlePasteAsync();

    internal async Task HandlePasteAsync()
    {
        if (!Enabled || accepting) return;
        if (!canAccept()) { report("Finish the current dialog or preparation, then press Ctrl+Shift+V again."); return; }
        accepting = true;
        try
        {
            // Capture the destination before a delayed phone clipboard provider can change focus.
            var target = platform.ReadTarget();
            using var clipboard = platform.ReadClipboard();
            if (clipboard is null) { report("Copy or cut files in Explorer first, then press Ctrl+Shift+V."); return; }
            if (clipboard.Sequence != platform.Sequence)
            { report("The clipboard changed. Copy or cut the files again."); return; }
            if (target is not null && platform.StillFocused(target)) await accept(clipboard, target.Directory);
            else await prepare(clipboard);
        }
        catch (Exception error) { report("Stransfer paste could not start: " + error.Message); }
        finally { accepting = false; }
    }
    public void Dispose() { Enabled = false; platform.Pressed -= OnPressed; platform.Dispose(); }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}

internal interface IShortcutPlatform : IDisposable
{
    event Action? Pressed;
    bool TryRegister(out int error);
    void Unregister();
    ClipboardTransfer? ReadClipboard();
    uint Sequence { get; }
    ExplorerTarget? ReadTarget();
    bool StillFocused(ExplorerTarget target);
}

internal sealed class WindowShortcutPlatform(Window window) : IShortcutPlatform
{
    internal const int Id = 0x5354;
    internal const uint Modifiers = 0x4000 | 0x0002 | 0x0004; // NOREPEAT | CONTROL | SHIFT
    internal const uint Key = 0x56;
    private HwndSource? source;
    private bool registered;
    public event Action? Pressed;
    public bool TryRegister(out int error)
    {
        if (source is null)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
            source.AddHook(OnMessage);
        }
        registered = RegisterHotKey(source.Handle, Id, Modifiers, Key);
        error = registered ? 0 : Marshal.GetLastWin32Error();
        return registered;
    }
    internal static bool Matches(int message, IntPtr wParam, IntPtr lParam) =>
        message == 0x0312 && wParam.ToInt64() == Id
        && ((ulong)lParam.ToInt64() & 0xFFFF) == 6
        && (((ulong)lParam.ToInt64() >> 16) & 0xFFFF) == Key;
    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (registered && Matches(message, wParam, lParam)) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Unregister()
    {
        if (registered && source is not null) UnregisterHotKey(source.Handle, Id);
        registered = false;
    }
    public ClipboardTransfer? ReadClipboard() => ShellClipboardReader.Read();
    public uint Sequence => ExplorerPasteService.ClipboardSequence;
    public ExplorerTarget? ReadTarget() => ExplorerFolderResolver.Read();
    public bool StillFocused(ExplorerTarget target) => ExplorerFolderResolver.StillFocused(target);
    public void Dispose() { Unregister(); source?.RemoveHook(OnMessage); source = null; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
