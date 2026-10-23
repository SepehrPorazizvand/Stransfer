using System.IO;
using System.Windows;
using System.Windows.Media;
using Stransfer.App.Infrastructure;
using Stransfer.Core;

namespace Stransfer.App.ViewModels;

public sealed class JobViewModel : ObservableObject
{
    private TransferJobSnapshot job = new();
    private bool scheduled;
    private long lastBytes;
    private DateTime lastTime = DateTime.UtcNow;
    private double speed;
    private double peakSpeed;
    private readonly List<double> speedHistory = new();
    private bool isDetailed = true;

    public Guid Id => job.Id;
    public string Destination => job.DestinationDisplayName ?? job.DestinationDirectory;
    public string DestinationLocation => job.DestinationDirectory;
    public TransferOperation Operation => job.Operation;
    public string CopyMode => job.Operation == TransferOperation.Move ? "Safe move" : job.Verify ? "Verified" : "Fast";
    public string Title => $"{job.Operation} · " + (job.Sources.Count == 1 ? Path.GetFileName(job.Sources[0].TrimEnd('\\', '/')) : $"{job.Sources.Count:N0} items");
    public string OperationHeading => $"{(job.Operation == TransferOperation.Move ? "Moving" : "Copying")} {(job.TotalFiles > 0 ? $"{job.TotalFiles:N0} {(job.TotalFiles == 1 ? "item" : "items")}" : "files")}";
    public string Route => string.Join(" + ", job.Sources.Take(2)) + (job.Sources.Count > 2 ? $" (+{job.Sources.Count - 2})" : "") + " → " + Destination;
    public double Percent => job.Status == TransferStatus.Completed ? 100 : job.TotalBytes > 0 ? Math.Clamp(100d * job.BytesTransferred / job.TotalBytes, 0, 100) : 0;
    public string PercentText => $"{Percent:0.#}%";
    public string Progress => $"{Size(job.BytesTransferred)} / {Size(job.TotalBytes)} · {job.CompletedFiles:N0} / {job.TotalFiles:N0} files";
    public string FormattedTransferAmount => $"{Size(job.BytesTransferred)} of {Size(job.TotalBytes)}";
    public string Detail => job.Status == TransferStatus.Running ? (speed > 0 ? $"{Size((long)speed)}/s · " : "") + (job.CurrentFile ?? "Verifying and saving progress…") : job.Error ?? (scheduled ? "Waiting for the current transfer" : "");
    public string CurrentFile => job.CurrentFile ?? "";
    public string CurrentFileName => string.IsNullOrWhiteSpace(job.CurrentFile) ? "Preparing transfer…" : Path.GetFileName(job.CurrentFile);
    public string CurrentFileDirectory => string.IsNullOrWhiteSpace(job.CurrentFile) ? "" : (Path.GetDirectoryName(job.CurrentFile) ?? "");

    public string Status => scheduled && job.Status != TransferStatus.Running ? "Queued" : job.Status switch
    {
        TransferStatus.WaitingForDrive => "Waiting for drive",
        TransferStatus.Failed => "Needs attention",
        _ => job.Status.ToString()
    };

    public bool IsRunning => job.Status == TransferStatus.Running;
    public bool IsCompleted => job.Status == TransferStatus.Completed;
    public bool IsPaused => job.Status == TransferStatus.Paused;
    public bool IsFailed => job.Status == TransferStatus.Failed;
    public bool IsWaitingForDrive => job.Status == TransferStatus.WaitingForDrive;
    public bool CanPause => scheduled || job.Status == TransferStatus.Running;
    public bool CanResume => job.CanResume && !scheduled;
    public bool CanRemove => !CanPause;

    public bool IsDetailed
    {
        get => isDetailed;
        set => Set(ref isDetailed, value);
    }

    public string ToggleDetailsText => IsDetailed ? "Fewer details ▴" : "More details ▾";
    public RelayCommand ToggleDetailsCommand { get; }

    public string SpeedText => speed > 1024 ? $"{Size((long)speed)}/s" : (IsRunning ? "Calculating…" : "0 B/s");
    public string PeakSpeedText => peakSpeed > 0 ? $"Peak: {Size((long)peakSpeed)}/s" : "";

    public string RemainingTimeText
    {
        get
        {
            if (job.Status != TransferStatus.Running)
            {
                return job.Status switch
                {
                    TransferStatus.Paused => "Paused",
                    TransferStatus.Completed => "Transfer complete",
                    TransferStatus.WaitingForDrive => "Waiting for drive to reconnect",
                    TransferStatus.Failed => "Transfer interrupted",
                    _ => "Waiting in queue"
                };
            }
            long remaining = Math.Max(0, job.TotalBytes - job.BytesTransferred);
            if (speed <= 1024 || remaining == 0) return "Estimating time remaining…";
            double secs = remaining / speed;
            if (secs < 5) return "A few seconds remaining";
            if (secs < 60) return $"About {(int)secs} seconds remaining";
            if (secs < 3600) return $"About {(int)(secs / 60)} minutes remaining";
            return $"About {secs / 3600:0.#} hours remaining";
        }
    }

    public string ItemsRemainingText
    {
        get
        {
            int filesRemaining = Math.Max(0, job.TotalFiles - job.CompletedFiles);
            long bytesRemaining = Math.Max(0, job.TotalBytes - job.BytesTransferred);
            return $"{filesRemaining:N0} items remaining ({Size(bytesRemaining)})";
        }
    }

    public PointCollection SpeedGraphPoints { get; private set; } = new();
    public PointCollection SpeedLinePoints { get; private set; } = new();

    public JobViewModel()
    {
        ToggleDetailsCommand = new(_ =>
        {
            IsDetailed = !IsDetailed;
            Changed(nameof(ToggleDetailsText));
        });
        InitBlankChart();
    }

    private void InitBlankChart()
    {
        var poly = new PointCollection { new Point(0, 50), new Point(360, 50) };
        SpeedGraphPoints = poly;
        SpeedLinePoints = new PointCollection { new Point(0, 50), new Point(360, 50) };
    }

    public void Update(TransferJobSnapshot value, bool isScheduled)
    {
        double elapsed = (DateTime.UtcNow - lastTime).TotalSeconds;
        if (elapsed > .4)
        {
            double current = Math.Max(0, value.BytesTransferred - lastBytes) / elapsed;
            speed = speed == 0 ? current : speed * .65 + current * .35;
            if (speed > peakSpeed) peakSpeed = speed;
            lastBytes = value.BytesTransferred;
            lastTime = DateTime.UtcNow;

            if (value.Status == TransferStatus.Running)
            {
                speedHistory.Add(speed);
                if (speedHistory.Count > 35) speedHistory.RemoveAt(0);
                UpdateGraph();
            }
        }
        job = value;
        scheduled = isScheduled;
        Changed(null);
    }

    private void UpdateGraph()
    {
        if (speedHistory.Count < 2) return;
        double max = Math.Max(peakSpeed, 1024 * 1024); // at least 1 MB scale
        const double width = 360;
        const double height = 50;

        var line = new PointCollection();
        var fill = new PointCollection { new Point(0, height) };

        for (int i = 0; i < speedHistory.Count; i++)
        {
            double x = (double)i / (speedHistory.Count - 1) * width;
            double ratio = Math.Clamp(speedHistory[i] / max, 0.05, 0.95);
            double y = height - (ratio * (height - 6));
            var pt = new Point(x, y);
            line.Add(pt);
            fill.Add(pt);
        }

        fill.Add(new Point(width, height));
        SpeedLinePoints = line;
        SpeedGraphPoints = fill;
        Changed(nameof(SpeedLinePoints));
        Changed(nameof(SpeedGraphPoints));
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}
