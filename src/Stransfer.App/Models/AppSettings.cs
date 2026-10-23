using System.Text.Json;

namespace Stransfer.App.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool KeepInBackground { get; set; } = true;
    public bool UseForExplorerPaste { get; set; }
    public bool ExplorerContextMenu { get; set; }
    public bool VerifyCopies { get; set; }
    public int SmallFileWorkers { get; set; }
    public string LastDestination { get; set; } = "";
}

public sealed class SettingsStore
{
    public string DirectoryPath { get; } = Environment.GetEnvironmentVariable("STRANSFER_STATE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stransfer");
    public bool IsIsolated => Environment.GetEnvironmentVariable("STRANSFER_STATE_DIR") is not null;
    public string? LoadWarning { get; private set; }
    public AppSettings Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { LoadWarning = "Settings could not be loaded. Defaults are active. " + error.Message; return new(); }
    }
    public void Save(AppSettings settings)
    {
        var path = Path.Combine(DirectoryPath, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
