using System.Windows;
using System.Windows.Threading;
using Stransfer.App.Models;
using Stransfer.App.ViewModels;
using Stransfer.App.Views;
using Stransfer.Core;
using Forms = System.Windows.Forms;

namespace Stransfer.App.Services;

public sealed class AppRuntime
{
    private readonly Window window;
    private readonly SettingsStore store = new();
    private readonly AppSettings settings;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer;
    private readonly ExplorerPasteService explorer;
    private readonly DesktopDialogs dialogs;
    private readonly HashSet<Guid> prompted = new();
    private readonly Dictionary<Guid, uint> cutClipboard = new();
    private readonly HashSet<CompactTransferWindow> transferWindows = new();
    internal IReadOnlyCollection<CompactTransferWindow> TransferWindows => transferWindows.ToArray();
    private DateTime nextProbe;
    private DateTime nextUpdateCheck = DateTime.UtcNow.AddSeconds(10);
    private bool probing, exiting, started;
    public TransferCoordinator Coordinator { get; }
    public MainViewModel ViewModel { get; }
    public bool KeepInBackground => settings.KeepInBackground;
    public bool IsExiting => exiting;
    public AppRuntime(Window window)
    {
        this.window = window;
        settings = store.Load();
        Coordinator = new(Path.Combine(store.DirectoryPath, "jobs"));
        dialogs = new(window);
        ViewModel = new(Coordinator, dialogs, settings, SaveSettings, store.DirectoryPath);
        ViewModel.Settings.Updates = new(settings, SaveSettings, () => !window.IsVisible, ExitAsync, store.DirectoryPath, store.IsIsolated, Notify);
        explorer = new(window, AcceptPasteAsync, ChoosePasteDestinationAsync,
            text => { ViewModel.Status = text; Notify(text); }, ViewModel.Shortcuts.UpdateStatus,
            () => !exiting && !dialogs.IsOpen && !ViewModel.ResumeVisible && !ViewModel.Busy);
        ViewModel.Shortcuts.RetryRequested += SaveSettings;
        ViewModel.RevealRequested += Reveal;
        tray = new Forms.NotifyIcon { Text = "Stransfer — resumable file transfers", Visible = true };
        try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Stransfer", null, (_, _) => window.Dispatcher.BeginInvoke(Reveal));
        menu.Items.Add("Pause all transfers", null, (_, _) => window.Dispatcher.BeginInvoke(() => { foreach (var job in Coordinator.Jobs) { prompted.Add(job.Id); Coordinator.Pause(job.Id); } }));
        menu.Items.Add("Save progress and exit", null, (_, _) => window.Dispatcher.BeginInvoke(async () => await ExitAsync()));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => window.Dispatcher.BeginInvoke(Reveal);
        tray.BalloonTipClicked += (_, _) => window.Dispatcher.BeginInvoke(() =>
        {
            if (transferWindows.LastOrDefault() is { } progress) { progress.Show(); progress.WindowState = WindowState.Normal; progress.Activate(); }
            else Reveal();
        });
        Coordinator.Finished += (_, job) =>
        {
            prompted.Add(job.Id);
            if (job.Status == TransferStatus.WaitingForDrive) prompted.Remove(job.Id);
            if (job.Status == TransferStatus.Completed && cutClipboard.Remove(job.Id, out var sequence) && sequence == ExplorerPasteService.ClipboardSequence)
            { try { System.Windows.Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { } }
            if (job.Status is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.WaitingForDrive)
                Notify(job.Status == TransferStatus.Completed ? "Transfer completed." : job.Error ?? "Transfer paused.");
            ViewModel.Refresh();
            RefreshTransferWindows();
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += async (_, _) => { ViewModel.Refresh(); RefreshTransferWindows(); await ProbeResumeAsync(); await ProbeUpdatesAsync(); };
    }
    public async Task StartAsync(bool background, bool promptResume = true)
    {
        if (started) return; started = true;
        SaveSettings(); ViewModel.Refresh(); timer.Start();
        if (background) window.Hide();
        if (store.LoadWarning is { } warning) ViewModel.Status = warning;
        if (Coordinator.RecoveryWarnings.Count > 0) ViewModel.Status = string.Join("\n", Coordinator.RecoveryWarnings);
        if (promptResume) await ProbeResumeAsync();
    }
    private void SaveSettings()
    {
        try
        {
            store.Save(settings);
            WindowsRegistration.Apply(settings, store.IsIsolated);
            explorer?.SetEnabled(settings.UseForExplorerPaste && !store.IsIsolated);
            if (store.IsIsolated) ViewModel.Shortcuts.UpdateStatus(false, "Disabled in the isolated test profile.");
        }
        catch (Exception error) { if (ViewModel is not null) ViewModel.Status = "Settings could not be fully applied: " + error.Message; }
    }
    public async Task HandleArgumentsAsync(string[] arguments)
    {
        if (arguments.Contains("--exit")) { await ExitAsync(); return; }
        if (!arguments.Contains("--paste"))
        {
            var sources = arguments.Where(path => !path.StartsWith("--", StringComparison.Ordinal)
                && (File.Exists(path) || Directory.Exists(path))).ToArray();
            if (sources.Length > 0) await ChoosePasteDestinationAsync(new(sources, TransferOperation.Copy, 0));
            else if (!arguments.Contains("--background")) Reveal();
            return;
        }
        try
        {
            var index = Array.IndexOf(arguments, "--destination");
            if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException("A destination folder is required.");
            using var clip = ShellClipboardReader.Read() ?? throw new IOException("Copy or cut files in Explorer first.");
            await AcceptPasteAsync(clip, arguments[index + 1]);
        }
        catch (Exception error) { ViewModel.Status = error.Message; Notify(error.Message); }
    }
    internal async Task AcceptPasteAsync(ClipboardTransfer clipboard, string destination)
    {
        if (exiting) throw new IOException("Stransfer is shutting down. Paste again after restarting it.");
        window.Hide();
        var model = new CompactTransferViewModel(
            id => { prompted.Add(id); Coordinator.Pause(id); RefreshTransferWindows(); },
            id => { prompted.Add(id); Coordinator.Schedule(id); RefreshTransferWindows(); },
            Reveal, path => { try { DesktopDialogs.OpenFolder(path); } catch (Exception error) { Notify(error.Message); } });
        var progress = new CompactTransferWindow(model);
        transferWindows.Add(progress);
        progress.Closed += (_, _) => transferWindows.Remove(progress);
        progress.Show();
        try
        {
            var sources = clipboard.Shell is { } shell ? shell.ReadSources() : clipboard.Sources;
            var id = await ViewModel.CreateAndScheduleAsync(sources, destination, clipboard.Operation);
            prompted.Add(id);
            if (clipboard.Operation == TransferOperation.Move) cutClipboard[id] = clipboard.Sequence;
            model.Update(Coordinator.Jobs.Single(job => job.Id == id), Coordinator.IsScheduled(id));
        }
        catch (Exception error) { model.Fail(error.Message); ViewModel.Status = error.Message; }
    }
    private void RefreshTransferWindows()
    {
        if (transferWindows.Count == 0) return;
        var jobs = Coordinator.Jobs.ToDictionary(job => job.Id);
        foreach (var progress in transferWindows)
            if (progress.ViewModel.JobId is { } id && jobs.TryGetValue(id, out var job))
                progress.ViewModel.Update(job, Coordinator.IsScheduled(id));
    }
    internal async Task ChoosePasteDestinationAsync(ClipboardTransfer clipboard)
    {
        // A folder picker is the only necessary input when no Explorer destination is identifiable.
        if (dialogs.ChooseDestination() is { } destination) await AcceptPasteAsync(clipboard, destination);
    }
    private async Task ProbeResumeAsync()
    {
        if (probing || exiting || dialogs.IsOpen || ViewModel.ResumeVisible || transferWindows.Any(progress => progress.IsVisible) || DateTime.UtcNow < nextProbe) return;
        nextProbe = DateTime.UtcNow.AddSeconds(3); probing = true;
        try
        {
            var jobs = Coordinator.Jobs.Where(job => job.CanResume && !Coordinator.IsScheduled(job.Id)).ToArray();
            var availability = new List<(TransferJobSnapshot Job, bool Ready)>();
            foreach (var job in jobs) availability.Add((job, await Coordinator.AreDrivesAvailableAsync(job.Id)));
            if (exiting) return;
            foreach (var item in availability.Where(item => !item.Ready)) prompted.Remove(item.Job.Id);
            var ready = availability.Where(item => item.Ready && !prompted.Contains(item.Job.Id) && !Coordinator.IsScheduled(item.Job.Id)).Select(item => item.Job).ToArray();
            foreach (var job in ready) prompted.Add(job.Id);
            if (ready.Length > 0) ViewModel.ShowResume(ready);
        }
        catch (Exception error) { ViewModel.Status = "Drive check failed: " + error.Message; }
        finally { probing = false; }
    }
    private void Notify(string text)
    {
        if (exiting) return;
        tray.BalloonTipTitle = "Stransfer";
        tray.BalloonTipText = text.Length > 250 ? text[..250] : text;
        tray.ShowBalloonTip(4000);
    }
    private async Task ProbeUpdatesAsync()
    {
        if (exiting || store.IsIsolated || !settings.CheckForUpdates || DateTime.UtcNow < nextUpdateCheck) return;
        nextUpdateCheck = DateTime.UtcNow.AddHours(6);
        await ViewModel.Settings.Updates!.CheckAsync(automatic: true);
    }
    public void Reveal() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
    public async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        timer.Stop(); explorer.Dispose(); ViewModel.Status = "Saving progress…";
        foreach (var progress in transferWindows.ToArray()) progress.Close();
        try { await Coordinator.StopAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ViewModel.Status = "Device transfer stopped: " + error.Message; }
        finally { ViewModel.Settings.Updates?.Dispose(); tray.Visible = false; tray.Dispose(); Coordinator.Dispose(); System.Windows.Application.Current.Shutdown(); }
    }
}
