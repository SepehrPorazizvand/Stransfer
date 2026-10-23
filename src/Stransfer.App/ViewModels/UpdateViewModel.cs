using Stransfer.App.Infrastructure;
using Stransfer.App.Models;
using Stransfer.App.Services;

namespace Stransfer.App.ViewModels;

public sealed class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly GitHubUpdates client = new();
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly Func<bool> background;
    private readonly Func<Task> exit;
    private readonly string state;
    private readonly bool isolated;
    private readonly Action<string> notify;
    private AvailableUpdate? release;
    private CancellationTokenSource? download;
    private bool busy, consentVisible, downloading;
    private string status = "Checks release information only. Downloads always need your permission.";
    private double progress;
    public string CurrentVersion => "Version " + GitHubUpdates.DisplayVersion;
    public string Status { get => status; private set => Set(ref status, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanDownload)); Changed(nameof(CanCheck)); } }
    public bool Downloading { get => downloading; private set => Set(ref downloading, value); }
    public bool ConsentVisible { get => consentVisible; private set => Set(ref consentVisible, value); }
    public bool HasUpdate => release is not null;
    public bool CanDownload => HasUpdate && !Busy;
    public bool CanCheck => !Busy;
    public string ConsentText => release is null ? "" : $"Download version {release.Version.ToString(3)} ({JobViewModel.Size(release.Package.Size)}) and restart Stransfer? Active transfers will pause with saved progress. Your settings and transfer records are kept.";
    public bool AutomaticChecks
    {
        get => settings.CheckForUpdates;
        set { if (settings.CheckForUpdates == value) return; settings.CheckForUpdates = value; save(); Changed(); }
    }
    public AsyncCommand Check { get; }
    public RelayCommand RequestDownload { get; }
    public AsyncCommand AllowDownload { get; }
    public RelayCommand Later { get; }
    public RelayCommand Cancel { get; }

    public UpdateViewModel(AppSettings settings, Action save, Func<bool> background, Func<Task> exit,
        string state, bool isolated, Action<string> notify)
    {
        this.settings = settings; this.save = save; this.background = background; this.exit = exit;
        this.state = state; this.isolated = isolated; this.notify = notify;
        Check = new(async _ => await CheckAsync(automatic: false), Report);
        RequestDownload = new(_ => { if (CanDownload) ConsentVisible = true; });
        AllowDownload = new(async _ => await DownloadAsync(), Report);
        Later = new(_ => ConsentVisible = false);
        Cancel = new(_ => download?.Cancel());
    }

    public async Task CheckAsync(bool automatic)
    {
        if (Busy || ConsentVisible) return;
        if (isolated) { Status = "Online checks are disabled in this isolated test profile."; return; }
        Busy = true; Status = "Checking GitHub Releases…";
        try
        {
            var found = await client.CheckAsync();
            var announce = found is not null && release?.Version != found.Version;
            release = found; Changed(nameof(HasUpdate)); Changed(nameof(CanDownload)); Changed(nameof(ConsentText));
            Status = found is null ? "Stransfer is up to date." : $"Version {found.Version.ToString(3)} is available. Download only when you choose.";
            if (automatic && announce) notify("A new Stransfer version is available. Open Settings → Updates to review it.");
        }
        catch (Exception error) { Status = "Could not check for updates: " + error.Message; }
        finally { Busy = false; }
    }

    private async Task DownloadAsync()
    {
        // The confirmation is a distinct user action. Checking never fetches the package.
        if (!ConsentVisible || !CanDownload || release is null) return;
        var selected = release; ConsentVisible = false; Busy = true; Downloading = true; Progress = 0;
        download = new CancellationTokenSource();
        try
        {
            Status = "Downloading update…";
            var plan = await client.DownloadAsync(selected, state, Environment.ProcessPath!, background(),
                new Progress<double>(value => Progress = value), download.Token);
            Status = "Update verified. Saving progress and restarting…"; Downloading = false;
            await UpdateInstaller.LaunchAsync(state, plan, download.Token);
            await exit();
        }
        catch (OperationCanceledException) { Status = "Download cancelled. The current app was kept."; }
        catch (Exception error) { Report(error); }
        finally { download.Dispose(); download = null; Downloading = false; Busy = false; }
    }
    private void Report(Exception error) => Status = "Update could not finish: " + error.Message;
    public void Dispose() { download?.Cancel(); client.Dispose(); }
}
