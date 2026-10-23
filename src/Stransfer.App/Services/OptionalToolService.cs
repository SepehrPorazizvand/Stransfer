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
        return await RunPackageManagerAsync(InstallArguments(tool), "Installation");
    }
    public static async Task<string> RemoveAsync(OptionalTool tool)
    {
        if (!Catalog.Contains(tool)) throw new ArgumentException("Unknown tool.");
        if (!IsInstalled(tool)) return "Already removed.";
        return await RunPackageManagerAsync(RemoveArguments(tool), "Removal");
    }
    internal static string[] InstallArguments(OptionalTool tool) => ["install", "--id", tool.PackageId, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"];
    internal static string[] RemoveArguments(OptionalTool tool) => ["uninstall", "--id", tool.PackageId, "--exact", "--source", "winget", "--disable-interactivity"];
    private static async Task<string> RunPackageManagerAsync(string[] arguments, string action)
    {
        var start = new ProcessStartInfo("winget.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception launchError) when (launchError.NativeErrorCode == 2)
        { throw new IOException("Windows Package Manager (winget) is unavailable. Use Windows Settings > Apps or the tool's official website.", launchError); }
        using var process = started ?? throw new IOException("Windows Package Manager did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var details = (await output) + "\n" + (await error);
        if (process.ExitCode != 0) throw new IOException(action + " did not complete. " + details.Trim());
        return details.Trim();
    }
    public static bool IsInstalled(OptionalTool tool)
    {
        return InstallationDetails(tool) is not null;
    }
    public static string? InstallationDetails(OptionalTool tool)
    {
        if (!Catalog.Contains(tool)) throw new ArgumentException("Unknown tool.");
        if (tool.PackageId == "7zip.7zip")
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                if (File.Exists(Path.Combine(Environment.GetFolderPath(root), "7-Zip", "7zFM.exe"))) return "Installed";
            return null;
        }
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            roots.Add(Path.Combine(Environment.GetFolderPath(folder), "dotnet"));
        foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT_ARM64" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root) roots.Add(root);
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (entry.Length > 0 && File.Exists(Path.Combine(entry.Trim('"'), "dotnet.exe"))) roots.Add(entry.Trim('"'));
        var versions = new HashSet<string>();
        foreach (var root in roots)
        {
            try
            {
                var sdk = Path.Combine(root, "sdk");
                if (!Directory.Exists(sdk)) continue;
                foreach (var directory in Directory.EnumerateDirectories(sdk, "10.*"))
                    if (File.Exists(Path.Combine(directory, "dotnet.dll"))) versions.Add(Path.GetFileName(directory));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return versions.Count == 0 ? null : "Installed · " + string.Join(", ", versions.OrderDescending());
    }
    public static void OpenWebsite(OptionalTool tool) => Process.Start(new ProcessStartInfo(tool.Website) { UseShellExecute = true });
    public static void Launch(OptionalTool tool)
    {
        if (tool.PackageId == "Microsoft.DotNet.SDK.10") { OpenWebsite(tool); return; }
        var executable = File.Exists(tool.Executable) ? tool.Executable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7zFM.exe");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }
}
