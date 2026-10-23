using Stransfer.App.Infrastructure;
using Stransfer.App.Models;
using Stransfer.App.Services;
using Stransfer.Core;

namespace Stransfer.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly TransferCoordinator coordinator;
    private readonly DesktopDialogs dialogs;
    private readonly AppSettings settings;
    private readonly Action save;
    private readonly Dictionary<Guid, JobViewModel> rows = new();
    private string destination, status = "Ready", preflight = "Space, source availability and filesystem limits are checked before every start.";
    private bool move, history, busy, resumeVisible;
    private string resumeDetails = "";
    private IReadOnlyList<Guid> resumeIds = [];
    public ObservableCollection<string> Sources { get; } = new();
    public ObservableCollection<JobViewModel> Jobs { get; } = new();
    public SettingsViewModel Settings { get; }
    public ToolsViewModel Tools { get; }
    public ShortcutsViewModel Shortcuts { get; }
    public string Destination { get => destination; set => Set(ref destination, value); }
    public bool Move { get => move; set => Set(ref move, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string Preflight { get => preflight; private set => Set(ref preflight, value); }
    public bool Busy { get => busy; private set => Set(ref busy, value); }
    public bool History { get => history; private set { Set(ref history, value); Changed(nameof(QueueTitle)); Refresh(); } }
    public string QueueTitle => History ? "Completed transfers" : "Transfer queue";
    public string SourceSummary => Sources.Count == 0 ? "Drop files here or paste copied files" : $"{Sources.Count:N0} selected · " + string.Join(", ", Sources.Take(3).Select(Path.GetFileName));
    public bool ResumeVisible { get => resumeVisible; private set => Set(ref resumeVisible, value); }
    public string ResumeDetails { get => resumeDetails; private set => Set(ref resumeDetails, value); }
    public RelayCommand AddFiles { get; }
    public RelayCommand AddFolders { get; }
    public RelayCommand ClearSources { get; }
    public RelayCommand Paste { get; }
    public RelayCommand Browse { get; }
    public AsyncCommand Start { get; }
    public RelayCommand ShowQueue { get; }
    public RelayCommand ShowHistory { get; }
    public RelayCommand PauseJob { get; }
    public RelayCommand ResumeJob { get; }
    public AsyncCommand RemoveJob { get; }
    public RelayCommand OpenJob { get; }
    public RelayCommand ResumeAll { get; }
    public RelayCommand ResumeLater { get; }
    public RelayCommand OpenState { get; }
    public event Action? RevealRequested;
    public event Action? ComposerRequested;

    public MainViewModel(TransferCoordinator coordinator, DesktopDialogs dialogs, AppSettings settings, Action save, string statePath)
    {
        this.coordinator = coordinator; this.dialogs = dialogs; this.settings = settings; this.save = save;
        destination = settings.LastDestination;
        Settings = new(settings, save); Tools = new(Report); Shortcuts = new(Settings);
        AddFiles = new(_ => AddSources(dialogs.ChooseFiles())); AddFolders = new(_ => AddSources(dialogs.ChooseFolders()));
        ClearSources = new(_ => { Sources.Clear(); Changed(nameof(SourceSummary)); });
        Browse = new(_ => { if (dialogs.ChooseDestination() is { } folder) Destination = folder; });
        Paste = new(_ => { try { if (DesktopDialogs.ReadClipboard() is { } clip) { AddSources(clip.Sources); Move = clip.Operation == TransferOperation.Move; } else Status = "Copy or cut files in Explorer first."; } catch (Exception error) { Report(error); } });
        Start = new(async _ => { await CreateAndScheduleAsync(Sources.ToArray(), Destination, Move ? TransferOperation.Move : TransferOperation.Copy); Sources.Clear(); Changed(nameof(SourceSummary)); }, Report);
        ShowQueue = new(_ => History = false); ShowHistory = new(_ => History = true);
        PauseJob = new(value => { if (value is Guid id) coordinator.Pause(id); });
        ResumeJob = new(value => { if (value is Guid id) coordinator.Schedule(id); });
        RemoveJob = new(async value => { if (value is Guid id && dialogs.ConfirmRemove()) { await coordinator.RemoveAsync(id); rows.Remove(id); Refresh(); } }, Report);
        OpenJob = new(value => { if (value is string path) { try { DesktopDialogs.OpenFolder(path); } catch (Exception error) { Report(error); } } });
        OpenState = new(_ => DesktopDialogs.OpenFolder(statePath));
        ResumeAll = new(_ => { foreach (var id in resumeIds) coordinator.Schedule(id); ResumeVisible = false; });
        ResumeLater = new(_ => ResumeVisible = false);
    }
    public void AddSources(IEnumerable<string> paths)
    {
        var existing = Sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) if (existing.Add(path)) Sources.Add(path);
        Changed(nameof(SourceSummary));
    }
    public void PreparePaste(ClipboardTransfer clipboard)
    {
        Sources.Clear(); AddSources(clipboard.Sources);
        Move = clipboard.Operation == TransferOperation.Move;
        Destination = "";
        Status = "Choose a destination, then click Check and transfer.";
        ComposerRequested?.Invoke();
        RevealRequested?.Invoke();
    }
    public async Task<Guid> CreateAndScheduleAsync(string[] sources, string target, TransferOperation operation)
    {
        if (sources.Length == 0) throw new ArgumentException("Choose source files or folders.");
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Choose a destination folder.");
        Busy = true; Status = "Checking sources, destination space and filesystem limits…";
        try
        {
            var verify = settings.VerifyCopies;
            var options = new TransferOptions { SmallFileWorkers = settings.SmallFileWorkers };
            var job = await Task.Run(() => coordinator.Manager.CreateJobAsync(sources, target, operation, verify, options: options));
            if (job.Preflight is { } report) Preflight = $"Ready · {report.FileCount:N0} files · {JobViewModel.Size(report.RequiredBytes + report.SafetyMarginBytes)} needed · {JobViewModel.Size(report.AvailableBytes)} free · {JobViewModel.Size(report.ReservedByQueuedJobsBytes)} planned by other jobs · {report.FileSystem}";
            settings.LastDestination = job.DestinationDirectory; save();
            History = false; coordinator.Schedule(job.Id); Refresh(); Status = "Transfer queued. Existing files are kept with numbered names.";
            return job.Id;
        }
        catch (TransferPreflightException error) { Preflight = error.Message; throw; }
        finally { Busy = false; }
    }
    public void ShowResume(IReadOnlyList<TransferJobSnapshot> jobs)
    {
        resumeIds = jobs.Select(job => job.Id).ToArray();
        ResumeDetails = $"{jobs.Count:N0} interrupted transfer(s) are ready. Resume from saved progress?\n\n" + string.Join("\n", jobs.Take(4).Select(job => string.Join(" + ", job.Sources.Select(Path.GetFileName)) + " → " + job.DestinationDirectory));
        ResumeVisible = true; RevealRequested?.Invoke();
    }
    public void Refresh()
    {
        var all = coordinator.Jobs;
        foreach (var job in all)
        {
            if (!rows.TryGetValue(job.Id, out var row)) rows[job.Id] = row = new();
            row.Update(job, coordinator.IsScheduled(job.Id));
        }
        var visible = all.Where(job => (job.Status == TransferStatus.Completed) == History).Select(job => rows[job.Id]).ToArray();
        for (int index = Jobs.Count - 1; index >= 0; index--) if (!visible.Contains(Jobs[index])) Jobs.RemoveAt(index);
        for (int index = 0; index < visible.Length; index++)
            if (index >= Jobs.Count) Jobs.Add(visible[index]);
            else if (Jobs[index] != visible[index]) { Jobs.Remove(visible[index]); Jobs.Insert(index, visible[index]); }
    }
    public void Report(Exception error) { Status = error.Message; RevealRequested?.Invoke(); }
}
