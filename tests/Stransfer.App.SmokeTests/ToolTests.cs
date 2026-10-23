using Stransfer.App.Services;
using Stransfer.App.ViewModels;

namespace Stransfer.App.SmokeTests;

internal static class ToolTests
{
    public static async Task RunAsync()
    {
        bool installed = true;
        int installs = 0, removals = 0;
        Exception? failure = null;
        var tool = OptionalToolService.Catalog.Single(item => item.PackageId == "Microsoft.DotNet.SDK.10");
        var model = new ToolViewModel(tool, error => failure = error, () => Task.FromResult(installed ? "Installed · 10.0.402" : null),
            () => { installed = true; installs++; return Task.CompletedTask; }, () => { installed = false; removals++; return Task.CompletedTask; });
        await model.RefreshAsync();
        Require(model.IsInstalled && model.ActionLabel == "Remove" && model.Status.Contains("10.0.402"), "Installed SDK still showed Install.");
        model.Manage.Execute(null);
        Require(removals == 1 && installs == 0 && !model.IsInstalled && model.ActionLabel == "Install", "Remove did not refresh installation state.");
        model.Manage.Execute(null);
        Require(installs == 1 && model.IsInstalled && model.ActionLabel == "Remove", "Installation did not refresh the Remove action.");
        installed = false; await model.RefreshAsync();
        Require(!model.IsInstalled && model.ActionLabel == "Install" && failure is null, "External removal was not recognized.");
        var arguments = OptionalToolService.RemoveArguments(tool);
        Require(arguments[0] == "uninstall" && arguments.Contains("--exact") && arguments.Contains(tool.PackageId) && !arguments.Contains("--all"), "Removal did not target only the selected tool.");
        Console.WriteLine("PASS  installed SDK shows Remove, install/removal refresh state, external changes refresh, exact package removal arguments (no real installation or removal)");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
