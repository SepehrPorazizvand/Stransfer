using Stransfer.App.Infrastructure;
using Stransfer.App.Models;

namespace Stransfer.App.ViewModels;

public sealed class SettingsViewModel(AppSettings settings, Action save) : ObservableObject
{
    public UpdateViewModel? Updates { get; internal set; }
    public bool StartWithWindows { get => settings.StartWithWindows; set { if (settings.StartWithWindows == value) return; settings.StartWithWindows = value; Save(); } }
    public bool KeepInBackground { get => settings.KeepInBackground; set { if (settings.KeepInBackground == value) return; settings.KeepInBackground = value; Save(); } }
    public bool UseForExplorerPaste { get => settings.UseForExplorerPaste; set { if (settings.UseForExplorerPaste == value) return; settings.UseForExplorerPaste = value; Save(); } }
    public bool ExplorerContextMenu { get => settings.ExplorerContextMenu; set { if (settings.ExplorerContextMenu == value) return; settings.ExplorerContextMenu = value; Save(); } }
    public bool VerifyCopies { get => settings.VerifyCopies; set { if (settings.VerifyCopies == value) return; settings.VerifyCopies = value; Save(); } }
    public string[] CopyModes { get; } = ["Fast — native copy with saved progress", "Verified — SHA-256 read-back"];
    public int CopyModeIndex
    {
        get => settings.VerifyCopies ? 1 : 0;
        set { if (value is not (0 or 1)) return; VerifyCopies = value == 1; Changed(); }
    }
    public int WorkerIndex
    {
        get => Array.IndexOf(WorkerValues, settings.SmallFileWorkers);
        set { if (value < 0 || value >= WorkerValues.Length) return; settings.SmallFileWorkers = WorkerValues[value]; Save(); }
    }
    private static readonly int[] WorkerValues = [0, 1, 2, 4, 8];
    public string[] WorkerLabels { get; } = ["Auto (detect storage)", "1 — sequential", "2 — small files", "4 — small files", "8 — small files"];
    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? property = null) { save(); Changed(property); }
}
