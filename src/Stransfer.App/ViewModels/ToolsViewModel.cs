using Stransfer.App.Infrastructure;
using Stransfer.App.Services;

namespace Stransfer.App.ViewModels;

public sealed class ToolViewModel : ObservableObject
{
    private readonly Func<Task<string?>> inspect;
    private readonly Func<Task> install;
    private readonly Func<Task> remove;
    private string status = "Checking installation…";
    private bool installed, ready, busy;
    public OptionalTool Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsInstalled { get => installed; private set { if (Set(ref installed, value)) Changed(nameof(ActionLabel)); } }
    public bool IsReady => ready && !busy;
    public string ActionLabel => IsInstalled ? "Remove" : "Install";
    public string OpenLabel => Tool.PackageId == "Microsoft.DotNet.SDK.10" ? "SDK documentation" : "Open 7-Zip";
    public AsyncCommand Manage { get; }
    public RelayCommand Website { get; }
    public RelayCommand Open { get; }
    public ToolViewModel(OptionalTool tool, Action<Exception> onError)
        : this(tool, onError, () => Task.Run(() => OptionalToolService.InstallationDetails(tool)),
            async () => { await OptionalToolService.InstallAsync(tool); }, async () => { await OptionalToolService.RemoveAsync(tool); }) { }
    internal ToolViewModel(OptionalTool tool, Action<Exception> onError, Func<Task<string?>> inspect, Func<Task> install, Func<Task> remove)
    {
        Tool = tool; this.inspect = inspect; this.install = install; this.remove = remove;
        Manage = new(async _ =>
        {
            if (!IsReady) return;
            busy = true; Changed(nameof(IsReady));
            Status = IsInstalled ? "Removing with Windows Package Manager…" : "Installing with Windows Package Manager…";
            try
            {
                if (IsInstalled) await remove(); else await install();
                await InspectAsync();
            }
            catch { Status = "Action did not complete. Refresh status or use Windows Settings > Apps."; throw; }
            finally { busy = false; Changed(nameof(IsReady)); }
        }, onError);
        Website = new(_ => OptionalToolService.OpenWebsite(tool));
        Open = new(_ => { try { OptionalToolService.Launch(tool); } catch (Exception error) { onError(error); } });
    }
    public async Task RefreshAsync()
    {
        if (busy) return;
        busy = true; Changed(nameof(IsReady));
        try { await InspectAsync(); }
        finally { busy = false; Changed(nameof(IsReady)); }
    }
    private async Task InspectAsync()
    {
        var details = await inspect();
        IsInstalled = details is not null;
        Status = details ?? "Not installed · optional";
        ready = true;
    }
}

public sealed class ToolsViewModel
{
    public IReadOnlyList<ToolViewModel> Items { get; }
    public AsyncCommand Refresh { get; }
    public ToolsViewModel(Action<Exception> onError)
    {
        Items = OptionalToolService.Catalog.Select(tool => new ToolViewModel(tool, onError)).ToArray();
        Refresh = new(async _ => await Task.WhenAll(Items.Select(item => item.RefreshAsync())), onError);
    }
    public string Runtime => $".NET {Environment.Version} · running. The full build includes its runtime; the slim build uses the installed Desktop Runtime.";
}
