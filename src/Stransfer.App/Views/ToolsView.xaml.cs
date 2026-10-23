namespace Stransfer.App.Views;
public partial class ToolsView : System.Windows.Controls.UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is ViewModels.ToolsViewModel model) model.Refresh.Execute(null); };
    }
}
