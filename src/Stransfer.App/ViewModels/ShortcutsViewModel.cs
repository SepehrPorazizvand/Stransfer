using Stransfer.App.Infrastructure;

namespace Stransfer.App.ViewModels;

public sealed class ShortcutsViewModel : ObservableObject
{
    private string status = "Disabled.";
    private bool registered;
    public SettingsViewModel Settings { get; }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsRegistered { get => registered; private set => Set(ref registered, value); }
    public RelayCommand Retry { get; }
    public event Action? RetryRequested;
    public ShortcutsViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        Retry = new(_ => RetryRequested?.Invoke());
    }
    public void UpdateStatus(bool enabled, string text) { IsRegistered = enabled; Status = text; }
}
