using System.Windows;
using Stransfer.App.Services;
using Stransfer.App.ViewModels;

namespace Stransfer.App;

public partial class MainWindow : Window
{
    public AppRuntime Runtime { get; }
    public MainViewModel ViewModel => Runtime.ViewModel;
    public MainWindow(bool background = false)
    {
        InitializeComponent();
        Runtime = new(this); DataContext = ViewModel;
        ViewModel.ComposerRequested += () => Workspace.SelectedIndex = 0;
        Loaded += async (_, _) => await Runtime.StartAsync(background);
        Activated += (_, _) => ViewModel.Tools.Refresh.Execute(null);
        Closing += async (_, e) =>
        {
            if (Runtime.IsExiting) return;
            e.Cancel = true;
            if (Runtime.KeepInBackground) Hide(); else await Runtime.ExitAsync();
        };
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !ViewModel.ResumeVisible ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel.ResumeVisible || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        await Runtime.ChoosePasteDestinationAsync(new(paths, Stransfer.Core.TransferOperation.Copy, 0));
    }
}
