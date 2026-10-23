using System.Text.Json;
using Microsoft.Win32;

namespace Stransfer.App.Services;

public sealed record UpdatePlan(Guid Id, string Target, int ParentProcessId, long ParentStartTicks, string Sha256,
    string Version, bool Background, DateTimeOffset CreatedAt);

public static class UpdateInstaller
{
    private static readonly string[] StageFiles = ["Stransfer.exe", "package.zip", "plan.json", "ready", "started", "complete", "error.txt"];
    internal static string StageDirectory(string state, Guid id) => Path.Combine(Path.GetFullPath(state), "updates", id.ToString("N"));

    public static void CheckTarget(string target)
    {
        if (!Path.GetFileName(target).Equals("Stransfer.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            throw new IOException("Updates require running Stransfer.exe from its extracted folder.");
        RejectLinks(target);
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".stransfer-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Move Stransfer to a regular writable folder before updating.");
    }

    public static async Task LaunchAsync(string state, UpdatePlan plan, CancellationToken token)
    {
        var folder = StageDirectory(state, plan.Id);
        var candidate = Path.Combine(folder, "Stransfer.exe");
        await GitHubUpdates.VerifyHashAsync(candidate, plan.Sha256, token);
        var start = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(plan.Id.ToString("N"));
        using var helper = Process.Start(start) ?? throw new IOException("The update helper could not start.");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(folder, "ready")))
        {
            if (helper.HasExited || clock.Elapsed > TimeSpan.FromSeconds(30))
                throw new IOException(File.Exists(Path.Combine(folder, "error.txt")) ? File.ReadAllText(Path.Combine(folder, "error.txt")) : "The update helper did not start. The current app was kept.");
            await Task.Delay(100, token);
        }
    }

    public static async Task<int> ApplyAsync(string state, Guid id)
    {
        var folder = StageDirectory(state, id); string? target = null, backup = null; Process? restarted = null;
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(folder, "plan.json")), GitHubUpdates.JsonOptions)
                ?? throw new InvalidDataException("The update plan is missing.");
            target = plan.Target;
            var candidate = Path.Combine(folder, "Stransfer.exe");
            if (plan.Id != id || DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromHours(1) || plan.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(1)
                || !Path.GetFullPath(Environment.ProcessPath!).Equals(candidate, StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(plan.Version, out var version) || GitHubUpdates.Normalize(version) != GitHubUpdates.Normalize(GitHubUpdates.CurrentVersion))
                throw new InvalidDataException("The update plan is invalid or expired.");
            CheckTarget(target);
            await GitHubUpdates.VerifyHashAsync(candidate, plan.Sha256);
            Process? parent;
            try { parent = Process.GetProcessById(plan.ParentProcessId); } catch (ArgumentException) { parent = null; }
            using (parent)
            {
                if (parent is not null && (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks
                    || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The original process no longer matches this update.");
                await File.WriteAllTextAsync(Path.Combine(folder, "ready"), "ready");
                if (parent is not null) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            }
            backup = await ReplaceAsync(candidate, target, id, plan.Sha256);
            restarted = StartTarget(target, plan.Background, id);
            var clock = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(folder, "started")))
            {
                if (restarted.HasExited || clock.Elapsed > TimeSpan.FromSeconds(45)) throw new IOException("The new version did not finish starting.");
                await Task.Delay(150);
            }
            File.Delete(backup); backup = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Stransfer", writable: true);
                if (string.Equals(key?.GetValue("InstallLocation") as string, Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
                    key!.SetValue("DisplayVersion", plan.Version);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            await File.WriteAllTextAsync(Path.Combine(folder, "complete"), "complete");
            return 0;
        }
        catch (Exception error)
        {
            try
            {
                if (backup is not null && target is not null && File.Exists(backup))
                {
                    if (restarted is { HasExited: false })
                    {
                        using var exit = Process.Start(new ProcessStartInfo(target, "--exit") { UseShellExecute = false, CreateNoWindow = true });
                        await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    }
                    Restore(target, backup);
                    StartTarget(target, background: false, null).Dispose();
                }
            }
            catch (Exception rollback) { error = new IOException(error.Message + " Recovery: " + rollback.Message, error); }
            try { await File.WriteAllTextAsync(Path.Combine(folder, "error.txt"), "Update failed: " + error.Message); } catch { }
            return 1;
        }
        finally { restarted?.Dispose(); }
    }

    internal static async Task<string> ReplaceAsync(string candidate, string target, Guid id, string hash)
    {
        CheckTarget(target);
        var pending = Path.Combine(Path.GetDirectoryName(target)!, ".stransfer-" + id.ToString("N") + ".new");
        var backup = Path.Combine(Path.GetDirectoryName(target)!, ".stransfer-" + id.ToString("N") + ".previous");
        try
        {
            await using (var input = File.OpenRead(candidate))
            await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            { await input.CopyToAsync(output); output.Flush(flushToDisk: true); }
            await GitHubUpdates.VerifyHashAsync(pending, hash);
            try { File.Replace(pending, target, backup); }
            catch (IOException) when (File.Exists(pending) && File.Exists(target) && !File.Exists(backup))
            {
                // exFAT/FAT portable drives may not support ReplaceFile.
                File.Move(target, backup);
                try { File.Move(pending, target); }
                catch { File.Move(backup, target); throw; }
            }
            return backup;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    internal static void Restore(string target, string backup) => File.Move(backup, target, overwrite: true);
    private static Process StartTarget(string target, bool background, Guid? updated)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        if (background) start.ArgumentList.Add("--background");
        if (updated is { } id) { start.ArgumentList.Add("--updated"); start.ArgumentList.Add(id.ToString("N")); }
        return Process.Start(start) ?? throw new IOException("Stransfer could not restart.");
    }

    public static void Acknowledge(string state, Guid id)
    {
        var folder = StageDirectory(state, id);
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(folder, "plan.json")), GitHubUpdates.JsonOptions);
        if (plan?.Id == id && string.Equals(plan.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(Path.Combine(folder, "started"), "started");
            _ = CleanupWhenCompleteAsync(state, id);
        }
    }

    private static async Task CleanupWhenCompleteAsync(string state, Guid id)
    {
        var folder = StageDirectory(state, id);
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            if (File.Exists(Path.Combine(folder, "complete")))
            {
                try { DeleteStage(state, id); return; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void DeleteStage(string state, Guid id)
    {
        var folder = StageDirectory(state, id);
        if (!Directory.Exists(folder)) return;
        RejectLinks(folder);
        foreach (var file in StageFiles) File.Delete(Path.Combine(folder, file));
        // Never remove unknown files or recursively delete a user directory.
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
}
