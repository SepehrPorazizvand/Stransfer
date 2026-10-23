using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Stransfer.App.Services;
using Stransfer.Core;

namespace Stransfer.App.SmokeTests;

internal static class ShortcutTests
{
    public static async Task RunAsync(Stransfer.App.MainWindow window)
    {
        var fake = new FakePlatform();
        var accepted = new List<(ClipboardTransfer Clip, string Target)>();
        var prepared = new List<ClipboardTransfer>();
        string report = "", status = "";
        bool allowed = true;
        TaskCompletionSource? hold = null;
        using var service = new ExplorerPasteService(fake, async (clip, target) =>
        {
            accepted.Add((clip, target));
            if (hold is not null) await hold.Task;
        }, prepared.Add, text => report = text, (_, text) => status = text, () => allowed);
        fake.Clipboard = new([@"C:\source\file.txt"], TransferOperation.Move, 10);
        fake.Target = new((IntPtr)1, (IntPtr)2, @"C:\destination");
        fake.Sequence = 10;
        await service.HandlePasteAsync();
        Require(accepted.Count == 0 && fake.Reads == 0, "Disabled shortcut read clipboard data.");
        fake.Conflict = true; service.SetEnabled(true);
        Require(!service.Enabled && status.Contains("Another application"), "Hotkey conflict was hidden.");
        fake.Conflict = false; service.SetEnabled(true);
        await service.HandlePasteAsync();
        Require(accepted.Count == 1 && accepted[0].Clip.Operation == TransferOperation.Move
            && accepted[0].Target == @"C:\destination", "Paste lost the cut operation or destination.");
        fake.Target = null; await service.HandlePasteAsync();
        Require(prepared.Count == 1 && accepted.Count == 1, "Unknown target should open destination selection.");
        fake.Target = new((IntPtr)1, (IntPtr)2, @"C:\other"); fake.Focused = false;
        await service.HandlePasteAsync();
        Require(prepared.Count == 2 && accepted.Count == 1, "Changed focus used an unsafe destination.");
        fake.Focused = true; fake.Sequence = 11;
        await service.HandlePasteAsync();
        Require(accepted.Count == 1 && report.Contains("clipboard changed"), "Changed clipboard started a transfer.");
        fake.Sequence = 10; allowed = false;
        await service.HandlePasteAsync();
        Require(accepted.Count == 1 && report.Contains("dialog"), "Resume/modal guard was bypassed.");
        allowed = true; fake.Clipboard = null;
        await service.HandlePasteAsync();
        Require(report.Contains("Copy or cut"), "Missing file clipboard was silently ignored.");
        fake.Clipboard = new([@"C:\source\file.txt"], TransferOperation.Copy, 10);
        hold = new();
        var first = service.HandlePasteAsync();
        await service.HandlePasteAsync();
        Require(accepted.Count == 2, "Repeated hotkey created duplicate jobs during preparation.");
        hold.SetResult(); await first;
        service.SetEnabled(false); await service.HandlePasteAsync();
        Require(accepted.Count == 2 && !service.Enabled, "Disabling the shortcut left it active.");

        var matching = (IntPtr)((0x56 << 16) | 6);
        Require(WindowShortcutPlatform.Matches(0x312, (IntPtr)WindowShortcutPlatform.Id, matching), "Ctrl+Shift+V not matched.");
        foreach (var value in new[] { (0x56 << 16) | 2, (0x56 << 16) | 7, (0x2D << 16) | 4 })
            Require(!WindowShortcutPlatform.Matches(0x312, (IntPtr)WindowShortcutPlatform.Id, (IntPtr)value), "Windows/other shortcut was claimed.");
        Require(WindowShortcutPlatform.Modifiers == 0x4006, "Registration must contain only Control, Shift and NoRepeat.");

        // Real RegisterHotKey and WM_HOTKEY dispatch to a test-owned HWND; no keystrokes or clipboard changes.
        using (var native = new WindowShortcutPlatform(window))
        {
            Require(native.TryRegister(out var error), $"Native Ctrl+Shift+V registration failed: {error}");
            int pressed = 0; native.Pressed += () => pressed++;
            var hwnd = new WindowInteropHelper(window).Handle;
            SendMessage(hwnd, 0x312, (IntPtr)WindowShortcutPlatform.Id, (IntPtr)((0x56 << 16) | 2));
            Require(pressed == 0, "Native message handler claimed Ctrl+V.");
            SendMessage(hwnd, 0x312, (IntPtr)WindowShortcutPlatform.Id, matching);
            Require(pressed == 1, "Native message handler did not deliver Ctrl+Shift+V.");
            native.Unregister();
            SendMessage(hwnd, 0x312, (IntPtr)WindowShortcutPlatform.Id, matching);
            Require(pressed == 1, "Unregistered native shortcut still triggered.");
        }
        window.ViewModel.Destination = @"C:\previous-destination";
        window.ViewModel.PreparePaste(new([@"C:\source\file.txt"], TransferOperation.Move, 10));
        Require(window.ViewModel.Destination == "" && window.ViewModel.Move
            && window.ViewModel.Sources.SequenceEqual(new[] { @"C:\source\file.txt" }), "Destination selection reused stale state.");
        window.ViewModel.Sources.Clear(); window.ViewModel.Move = false;
        Console.WriteLine("PASS  exact Ctrl+Shift+V registration, native message dispatch, conflicts, retry, disable, clipboard/focus guards, duplicate suppression and destination selection");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private sealed class FakePlatform : IShortcutPlatform
    {
        public event Action? Pressed { add { } remove { } }
        public bool Conflict;
        public bool Focused = true;
        public int Reads;
        public ClipboardTransfer? Clipboard;
        public ExplorerTarget? Target;
        public uint Sequence { get; set; }
        public bool TryRegister(out int error) { error = Conflict ? 1409 : 0; return !Conflict; }
        public void Unregister() { }
        public ClipboardTransfer? ReadClipboard() { Reads++; return Clipboard; }
        public ExplorerTarget? ReadTarget() => Target;
        public bool StillFocused(ExplorerTarget target) => Focused;
        public void Dispose() { }
    }
}
