using System.Windows;
using System.Windows.Interop;
using Stransfer.App.Services;

namespace Stransfer.App;

public partial class App : Application
{
    private SingleInstance? instance;
    public App()
    {
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var applyIndex = Array.IndexOf(e.Args, "--apply-update");
        if (applyIndex >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = applyIndex + 1 < e.Args.Length && Guid.TryParseExact(e.Args[applyIndex + 1], "N", out var updateId)
                ? await UpdateInstaller.ApplyAsync(new Models.SettingsStore().DirectoryPath, updateId) : 1;
            Shutdown(result); return;
        }
        DispatcherUnhandledException += (_, error) =>
        {
            error.Handled = true;
            var directory = new Models.SettingsStore().DirectoryPath;
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "errors.log"), DateTimeOffset.Now + "\n" + error.Exception + "\n\n"); } catch { }
            MessageBox.Show("Stransfer encountered an error. Saved progress can be resumed after restarting.\n\n" + error.Exception.Message, "Stransfer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        instance = new();
        if (!instance.IsFirst)
        {
            try { await instance.SendAsync(e.Args); }
            catch (Exception error) { MessageBox.Show("The running instance could not receive this command.\n" + error.Message, "Stransfer"); }
            Shutdown(); return;
        }
        bool background = e.Args.Contains("--background") || e.Args.Contains("--paste");
        var window = new MainWindow(background);
        MainWindow = window;
        _ = instance.ListenAsync(arguments => Dispatcher.BeginInvoke(async () => await window.Runtime.HandleArgumentsAsync(arguments)));
        if (background)
        {
            // Create the hotkey/IPC window handle without briefly flashing the dashboard.
            new WindowInteropHelper(window).EnsureHandle();
            await window.Runtime.StartAsync(background: true, promptResume: !e.Args.Contains("--paste"));
        }
        else window.Show();
        await window.Runtime.HandleArgumentsAsync(e.Args);
        var updatedIndex = Array.IndexOf(e.Args, "--updated");
        if (updatedIndex >= 0 && updatedIndex + 1 < e.Args.Length && Guid.TryParseExact(e.Args[updatedIndex + 1], "N", out var completedId))
        {
            try { UpdateInstaller.Acknowledge(new Models.SettingsStore().DirectoryPath, completedId); }
            catch (Exception error) { window.ViewModel.Status = "Update startup acknowledgement failed: " + error.Message; }
        }
        SessionEnding += (_, _) => { foreach (var job in window.Runtime.Coordinator.Jobs) window.Runtime.Coordinator.Pause(job.Id); };
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
