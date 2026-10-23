using Microsoft.Win32;
using Stransfer.App.Models;

namespace Stransfer.App.Services;

public static class WindowsRegistration
{
    private const string MenuKey = "Stransfer.Paste";
    public static void Apply(AppSettings settings, bool isolated)
    {
        if (isolated) return;
        var executable = Environment.ProcessPath ?? throw new IOException("The executable path is unavailable.");
        // Framework-dependent apphost and full builds both have a stable executable.
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (settings.StartWithWindows) run.SetValue("Stransfer", $"\"{executable}\" --background");
            else run.DeleteValue("Stransfer", false);
        }
        Register(@"Software\Classes\Directory\Background\shell\" + MenuKey, "%V");
        Register(@"Software\Classes\Directory\shell\" + MenuKey, "%1");
        void Register(string path, string destination)
        {
            if (!settings.ExplorerContextMenu) { Registry.CurrentUser.DeleteSubKeyTree(path, false); return; }
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("", "Paste with Stransfer"); key.SetValue("Icon", executable);
            key.SetValue("Position", "Top");
            using var command = key.CreateSubKey("command");
            // A literal dot avoids a trailing backslash escaping the closing quote for a drive root.
            command.SetValue("", $"\"{executable}\" --paste --destination \"{destination}\\.\"");
        }
    }
}
