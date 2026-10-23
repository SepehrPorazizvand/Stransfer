using Stransfer.App.Infrastructure;
using Stransfer.App.Services;

namespace Stransfer.App.ViewModels;

public sealed class ToolViewModel : ObservableObject
{
    public OptionalTool Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;
    private string status = "Optional · not bundled";
    public string Status { get => status; private set => Set(ref status, value); }
    public AsyncCommand Install { get; }
    public RelayCommand Website { get; }
    public RelayCommand Open { get; }
    public ToolViewModel(OptionalTool tool, Action<Exception> onError)
    {
        Tool = tool;
        if (OptionalToolService.IsInstalled(tool)) Status = "Installed";
        Install = new(async _ =>
        {
            Status = "Installing with Windows Package Manager…";
            try { await OptionalToolService.InstallAsync(tool); Status = "Installed"; }
            catch { Status = "Installation failed. Use the official website or retry."; throw; }
        }, onError);
        Website = new(_ => OptionalToolService.OpenWebsite(tool));
        Open = new(_ => { try { OptionalToolService.Launch(tool); } catch (Exception error) { onError(error); } });
    }
}

public sealed class ToolsViewModel(Action<Exception> onError)
{
    public IReadOnlyList<ToolViewModel> Items { get; } = OptionalToolService.Catalog.Select(tool => new ToolViewModel(tool, onError)).ToArray();
    public string Runtime => $".NET {Environment.Version} · running. The full build includes its runtime; the slim build uses the installed Desktop Runtime.";
}
