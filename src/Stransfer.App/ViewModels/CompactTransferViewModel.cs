using Stransfer.App.Infrastructure;
using Stransfer.Core;

namespace Stransfer.App.ViewModels;

public sealed class CompactTransferViewModel : ObservableObject
{
    private bool preparing = true;
    private string message = "Checking files and available space…";
    public Guid? JobId { get; private set; }
    public JobViewModel Job { get; } = new();
    public bool IsPreparing => preparing;
    public bool HasJob => JobId.HasValue;
    public bool HasMessage => !preparing && !string.IsNullOrWhiteSpace(message);
    public string Message => message;
    public string Heading => HasJob ? Job.OperationHeading : preparing ? "Preparing transfer…" : "Transfer could not start";
    public string WindowTitle => HasJob ? $"{Job.PercentText} · Stransfer" : "Stransfer";
    public string StatusDetail => $"{Job.Status} · {Job.CopyMode}";
    public string CurrentFile => Job.IsCompleted ? "Transfer complete" : Job.IsPaused ? "Transfer paused"
        : Job.IsWaitingForDrive ? "Reconnect the original device" : Job.IsFailed ? "Transfer needs attention" : Job.CurrentFileName;
    public RelayCommand Pause { get; }
    public RelayCommand Resume { get; }
    public RelayCommand OpenApp { get; }
    public RelayCommand OpenDestination { get; }

    public CompactTransferViewModel(Action<Guid> pause, Action<Guid> resume, Action openApp, Action<string> openDestination)
    {
        Pause = new(_ => { if (JobId is { } id) pause(id); });
        Resume = new(_ => { if (JobId is { } id) resume(id); });
        OpenApp = new(_ => openApp());
        OpenDestination = new(_ => { if (HasJob) openDestination(Job.DestinationLocation); });
    }
    public void Update(TransferJobSnapshot snapshot, bool scheduled)
    {
        JobId = snapshot.Id; preparing = false; message = snapshot.Error ?? "";
        Job.Update(snapshot, scheduled); Changed(null);
    }
    public void Fail(string error) { preparing = false; message = error; Changed(null); }
}
