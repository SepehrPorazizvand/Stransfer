using System.Windows;
using Stransfer.App.ViewModels;

namespace Stransfer.App.Views;

public partial class CompactTransferWindow : Window
{
    public CompactTransferViewModel ViewModel { get; }
    public bool IsClosed { get; private set; }
    public CompactTransferWindow(CompactTransferViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        Closed += (_, _) => IsClosed = true;
    }
}
