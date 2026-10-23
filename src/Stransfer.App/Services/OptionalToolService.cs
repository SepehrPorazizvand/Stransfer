namespace Stransfer.App.Services;

public sealed record OptionalTool(string Name, string PackageId, string Description, string Website, string Executable);

public static class OptionalToolService
{
    public static OptionalTool[] Catalog { get; } =
    [
        new("7-Zip", "7zip.7zip", "Create or extract archives. Opens as a separate tool; file transfers do not require it.", "https://www.7-zip.org/", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7zFM.exe")),
        new(".NET 10 SDK", "Microsoft.DotNet.SDK.10", "Build Stransfer from source and run its tests. Intended for development; transfers do not require the SDK.", "https://dotnet.microsoft.com/download/dotnet/10.0", "dotnet.exe")
    ];
    public static async Task<string> InstallAsync(OptionalTool tool)
    {
        if (!Catalog.Contains(tool)) throw new ArgumentException("Unknown tool.");
        if (IsInstalled(tool)) return "Already installed.";
        var start = new ProcessStartInfo("winget.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "install", "--id", tool.PackageId, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" }) start.ArgumentList.Add(argument);
        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception launchError) when (launchError.NativeErrorCode == 2)
        { throw new IOException("Windows Package Manager (winget) is unavailable. Use the tool's official website to install it.", launchError); }
        using var process = started ?? throw new IOException("Windows Package Manager did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var details = (await output) + "\n" + (await error);
        if (process.ExitCode != 0) throw new IOException("Installation did not complete. " + details.Trim());
        return details.Trim();
    }
    public static bool IsInstalled(OptionalTool tool)
    {
        if (File.Exists(tool.Executable)) return true;
        if (tool.PackageId != "Microsoft.DotNet.SDK.10") return false;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "sdk");
        try { return Directory.Exists(sdk) && Directory.EnumerateDirectories(sdk, "10.*").Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    public static void OpenWebsite(OptionalTool tool) => Process.Start(new ProcessStartInfo(tool.Website) { UseShellExecute = true });
    public static void Launch(OptionalTool tool)
    {
        if (tool.PackageId == "Microsoft.DotNet.SDK.10") { OpenWebsite(tool); return; }
        Process.Start(new ProcessStartInfo(tool.Executable) { UseShellExecute = true });
    }
}
