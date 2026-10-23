using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Stransfer.Installer;

public partial class MainWindow : Window
{
    private readonly string installDir;
    private bool isDone;

    public MainWindow()
    {
        InitializeComponent();
        installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Stransfer");
        TxtPath.Text = installDir;

        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
        {
            Loaded += async (_, _) =>
            {
                try { await DoInstallAsync(contextMenu: true, pasteShortcuts: true, startMenu: true, desktop: true); Application.Current.Shutdown(); }
                catch { Application.Current.Shutdown(1); }
            };
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (isDone)
        {
            if (ChkLaunch.IsChecked == true)
            {
                var exe = Path.Combine(installDir, "Stransfer.exe");
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir });
            }
            Close();
            return;
        }

        bool contextMenu = ChkContextMenu.IsChecked == true;
        bool pasteShortcuts = ChkPasteShortcuts.IsChecked == true;
        bool startMenu = ChkStartMenu.IsChecked == true;
        bool desktop = ChkDesktopShortcut.IsChecked == true;

        BtnInstall.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        PrgProgress.Visibility = Visibility.Visible;
        PrgProgress.IsIndeterminate = true;

        try
        {
            await DoInstallAsync(contextMenu, pasteShortcuts, startMenu, desktop);
            PrgProgress.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Stransfer has been installed successfully!";
            BtnInstall.Content = "Finish";
            BtnInstall.IsEnabled = true;
            isDone = true;
        }
        catch (Exception error)
        {
            PrgProgress.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Installation failed: " + error.Message;
            BtnCancel.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task DoInstallAsync(bool contextMenu, bool pasteShortcuts, bool startMenu, bool desktop)
    {
        TxtStatus.Text = "Saving progress in the installed application…";
        await Task.Run(async () =>
        {
            var installedExe = Path.Combine(installDir, "Stransfer.exe");
            foreach (var p in Process.GetProcessesByName("Stransfer"))
            {
                using (p)
                {
                    if (!string.Equals(p.MainModule?.FileName, installedExe, StringComparison.OrdinalIgnoreCase)) continue;
                    using var request = Process.Start(new ProcessStartInfo(installedExe, "--exit") { UseShellExecute = false, CreateNoWindow = true });
                    try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                    catch (TimeoutException) { throw new IOException("Stransfer is still saving progress. Exit it from the tray, then retry setup."); }
                }
            }
        });

        TxtStatus.Text = "Extracting application files…";
        await Task.Run(() =>
        {
            Directory.CreateDirectory(installDir);

            // Extract embedded payload.zip
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) throw new IOException("The installer payload is missing. Rebuild the full installer.");
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var archive = new ZipArchive(stream);
                var root = Path.GetFullPath(installDir) + Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                    if (!Path.GetFullPath(Path.Combine(installDir, entry.FullName)).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The installer payload contains an invalid path.");
                if (!archive.Entries.Any(entry => entry.FullName == "Stransfer.exe")) throw new IOException("The application is missing from the installer payload.");
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destPath = Path.Combine(installDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
        });

        var targetExe = Path.Combine(installDir, "Stransfer.exe");

        TxtStatus.Text = "Configuring settings…";
        await Task.Run(() =>
        {
            var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stransfer");
            Directory.CreateDirectory(stateDir);
            var settingsPath = Path.Combine(stateDir, "settings.json");
            var settings = File.Exists(settingsPath)
                ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? throw new IOException("Existing settings are invalid. Keep a backup before repairing them.")
                : new JsonObject { ["StartWithWindows"] = false, ["KeepInBackground"] = true, ["VerifyCopies"] = false, ["SmallFileWorkers"] = 0, ["LastDestination"] = "" };
            settings["UseForExplorerPaste"] = pasteShortcuts;
            settings["ExplorerContextMenu"] = contextMenu;
            File.WriteAllText(settingsPath + ".tmp", settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(settingsPath + ".tmp", settingsPath, true);
        });

        if (contextMenu)
        {
            TxtStatus.Text = "Registering Windows Explorer context menu…";
            await Task.Run(() =>
            {
                RegisterMenu(@"Software\Classes\Directory\Background\shell\Stransfer.Paste", "%V", targetExe);
                RegisterMenu(@"Software\Classes\Directory\shell\Stransfer.Paste", "%1", targetExe);
            });
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\Stransfer.Paste", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\Stransfer.Paste", false);
        }

        TxtStatus.Text = "Creating shortcuts…";
        await Task.Run(() =>
        {
            if (startMenu)
            {
                var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                CreateShortcut(Path.Combine(startMenuDir, "Stransfer.lnk"), targetExe, installDir);
            }
            if (desktop)
            {
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopDir, "Stransfer.lnk"), targetExe, installDir);
            }

            try
            {
                var sendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
                CreateShortcut(Path.Combine(sendTo, "Stransfer.lnk"), targetExe, installDir);
            }
            catch { }
        });

        TxtStatus.Text = "Registering uninstaller…";
        await Task.Run(() =>
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Stransfer");
                key.SetValue("DisplayName", "Stransfer");
                key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
                key.SetValue("Publisher", "Stransfer");
                key.SetValue("DisplayIcon", targetExe + ",0");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            catch { }
        });
    }

    private static void RegisterMenu(string path, string destination, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("", "Paste with Stransfer");
            key.SetValue("Icon", exePath);
            key.SetValue("Position", "Top");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{exePath}\" --paste --destination \"{destination}\\.\"");
        }
        catch { }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir)
    {
        try
        {
            var psScript = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{shortcutPath.Replace("'", "''")}');$s.TargetPath='{targetPath.Replace("'", "''")}';$s.WorkingDirectory='{workingDir.Replace("'", "''")}';$s.IconLocation='{targetPath.Replace("'", "''")},0';$s.Description='Stransfer - resumable file transfers';$s.Save();";
            var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + psScript + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }
    }
}
