using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Stransfer.Core;
using DesktopApp = Stransfer.App.App;
using MainWindow = Stransfer.App.MainWindow;

namespace Stransfer.App.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Contains("--clipboard-formats"))
        {
            Console.WriteLine(JsonSerializer.Serialize(Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? []));
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "Stransfer-ui-tests", Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, "state");
        var source = Path.Combine(root, "source", "smoke.txt");
        var destination = Path.Combine(root, "destination");
        var previousState = Environment.GetEnvironmentVariable("STRANSFER_STATE_DIR");
        DesktopApp? app = null;
        MainWindow? window = null;
        var errors = new List<Exception>();
        using var bindingTrace = new BindingErrorListener();
        var bindingSource = PresentationTraceSources.DataBindingSource;
        var previousTraceLevel = bindingSource.Switch.Level;
        var result = 1;
        try
        {
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(destination);
            const string content = "Stransfer WPF resume smoke test — انتقال قابل ادامه\n";
            File.WriteAllText(source, content);
            File.WriteAllText(Path.Combine(state, "settings.json"), JsonSerializer.Serialize(new
            {
                StartWithWindows = false,
                TrayNoticeShown = true,
                LastDestination = destination,
            }));
            Environment.SetEnvironmentVariable("STRANSFER_STATE_DIR", state);
            Guid jobId;
            using (var seed = new TransferManager(Path.Combine(state, "jobs")))
                jobId = seed.CreateJobAsync([source], destination).GetAwaiter().GetResult().Id;

            // Load the real App.xaml resources without App.Run/OnStartup, mutexes,
            // single-instance listeners, or startup registration.
            app = new DesktopApp();
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, error) =>
            {
                errors.Add(error.Exception);
                error.Handled = true;
            };
            bindingSource.Switch.Level = SourceLevels.Error;
            bindingSource.Listeners.Add(bindingTrace);
            window = new MainWindow(background: false);
            app.MainWindow = window;
            window.Show();
            window.UpdateLayout();
            var overlay = Find<FrameworkElement>(window, "ResumeOverlay");
            PumpUntil(() => overlay.Visibility == Visibility.Visible, errors, "Resume prompt was not shown.");
            Require(window.ViewModel.Jobs.Count == 1 && window.ViewModel.Jobs[0].Id == jobId, "Recovered transfer row was not populated.");
            var jobsList = Find<ItemsControl>(window, "JobsList");
            Require(jobsList.Items.Count == 1, "The real Jobs ItemsControl did not receive the recovered row.");
            window.UpdateLayout();
            var progress = Descendants<ProgressBar>(jobsList).Single();
            var progressBinding = BindingOperations.GetBinding(progress, ProgressBar.ValueProperty);
            Require(progressBinding?.Mode == BindingMode.OneWay,
                "ProgressBar.Value must use OneWay binding because TransferRow.Percent is read-only.");
            Require(window.ActualWidth > 0 && window.ActualHeight > 0 && progress.IsMeasureValid && progress.IsArrangeValid,
                "The real row template was not measured and arranged.");
            AssertNoErrors(errors, bindingTrace);
            Console.WriteLine("PASS  real XAML resources, queued row layout, read-only progress binding, and resume overlay");

            // Raise the real button's routed Click event on this test's WPF window.
            // This reaches ResumeAll_Click and the normal scheduling/transfer lane.
            SavePreview(window, "queue.png");
            var resumeButton = Find<Button>(window, "ResumeAllButton");
            Invoke(resumeButton);
            var manager = window.Runtime.Coordinator.Manager;
            PumpUntil(() => manager.Jobs.Single(job => job.Id == jobId).Status == TransferStatus.Completed
                && window.ViewModel.Jobs.Count == 0 && overlay.Visibility == Visibility.Collapsed,
                errors, "Resume button did not complete the queued transfer.");
            Require(File.ReadAllText(Path.Combine(destination, "smoke.txt")) == content,
                "Resume produced different destination contents.");
            Require(File.ReadAllText(source) == content, "Copy changed its source contents.");
            var history = Find<Button>(window, "HistoryTab");
            Invoke(history);
            PumpUntil(() => window.ViewModel.Jobs.Count == 1 && window.ViewModel.Jobs[0].Id == jobId && window.ViewModel.Jobs[0].Percent == 100,
                errors, "Completed transfer did not appear in history.");
            window.UpdateLayout();
            Require(window.IsVisible && window.IsMeasureValid && window.IsArrangeValid,
                "Window did not remain responsive with valid layout after completion.");
            AssertNoErrors(errors, bindingTrace);
            Console.WriteLine("PASS  real resume Click handler, file copy, completed history row, and post-transfer layout");
            var workspace = Find<TabControl>(window, "Workspace");
            foreach (var index in new[] { 1, 2, 3, 0 })
            {
                workspace.SelectedIndex = index;
                window.UpdateLayout();
                PumpUntil(() => workspace.IsArrangeValid, errors, "Tab did not arrange.");
                SavePreview(window, index == 1 ? "settings.png" : index == 2 ? "tools.png" : index == 3 ? "shortcuts.png" : "transfers.png");
                AssertNoErrors(errors, bindingTrace);
            }
            Require(!window.ViewModel.Settings.UseForExplorerPaste && !window.ViewModel.Settings.ExplorerContextMenu,
                "Explorer integration must be opt-in.");
            workspace.SelectedIndex = 2; window.UpdateLayout();
            PumpUntil(() => window.ViewModel.Tools.Items.All(item => item.IsReady), errors, "Installed tools did not refresh.");
            var toolButtons = Descendants<Button>(Find<ItemsControl>(window, "ToolsList")).Where(button => button.Command is Stransfer.App.Infrastructure.AsyncCommand).ToArray();
            Require(toolButtons.Length == window.ViewModel.Tools.Items.Count && toolButtons.All(button => button.Content is "Install" or "Remove"), "Tools page still has a fixed Install label.");
            workspace.SelectedIndex = 3; window.UpdateLayout();
            var toggle = Find<CheckBox>(window, "EnablePasteShortcut");
            ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
            PumpUntil(() => window.ViewModel.Settings.UseForExplorerPaste, errors, "Shortcut toggle did not update settings.");
            Require(JsonNode.Parse(File.ReadAllText(Path.Combine(state, "settings.json")))!["UseForExplorerPaste"]!.GetValue<bool>(), "Shortcut setting was not persisted.");
            Invoke(Find<Button>(window, "RetryShortcut"));
            PumpUntil(() => Find<TextBlock>(window, "ShortcutStatus").Text.Contains("isolated"), errors, "Shortcut status did not update.");
            ((IToggleProvider)new CheckBoxAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
            PumpUntil(() => !window.ViewModel.Settings.UseForExplorerPaste, errors, "Shortcut toggle did not disable settings.");
            var shortcuts = ShortcutTests.RunAsync(window);
            PumpUntil(() => shortcuts.IsCompleted, errors, "Shortcut routing tests did not complete.");
            shortcuts.GetAwaiter().GetResult();
            var tools = ToolTests.RunAsync();
            PumpUntil(() => tools.IsCompleted, errors, "Tool state tests did not complete."); tools.GetAwaiter().GetResult();
            var shellCopy = ShellTransferTests.RunAsync(root);
            PumpUntil(() => shellCopy.IsCompleted, errors, "Virtual device copy did not complete."); shellCopy.GetAwaiter().GetResult();
            workspace.SelectedIndex = 0; window.UpdateLayout();
            Console.WriteLine("PASS  Settings, Tools and Shortcuts tabs, shortcut toggle persistence and registration status bindings");
            var composedSource = Path.Combine(root, "source", "composed.txt");
            File.WriteAllText(composedSource, "A fresh transfer from the composer.");
            window.ViewModel.AddSources([composedSource]);
            window.ViewModel.Destination = destination;
            Invoke(Find<Button>(window, "StartButton"));
            PumpUntil(() => File.Exists(Path.Combine(destination, "composed.txt")) && !window.ViewModel.Busy
                && manager.Jobs.Count == 2 && manager.Jobs.All(job => job.Status == TransferStatus.Completed),
                errors, "The composer did not preflight and complete a new transfer.");
            Require(File.ReadAllText(composedSource) == File.ReadAllText(Path.Combine(destination, "composed.txt")), "The composed transfer changed data.");
            Require(window.ViewModel.Preflight.Contains("needed") && window.ViewModel.Preflight.Contains("free"), "Preflight did not display destination capacity.");
            AssertNoErrors(errors, bindingTrace);
            Console.WriteLine("PASS  composer starts a new checked transfer off the UI thread and displays available space");
            TestSingleInstance(errors);
            TestCompletedRemoval(root, errors);
            result = 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL  WPF smoke test\n" + error);
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    var exit = window.Runtime.ExitAsync();
                    PumpUntil(() => exit.IsCompleted, errors, "Window did not finish its real exit path.");
                    exit.GetAwaiter().GetResult();
                    PumpUntil(() => !window.IsVisible, errors, "Application shutdown did not close its window.");
                    Require(!window.IsVisible, "Window remained visible after ExitAsync.");
                    Console.WriteLine("PASS  ExitAsync disposes tray and closes the test window");
                }
                else app?.Shutdown();
            }
            catch (Exception error)
            {
                result = 1;
                Console.Error.WriteLine("FAIL  WPF cleanup\n" + error);
            }
            bindingSource.Listeners.Remove(bindingTrace);
            bindingSource.Switch.Level = previousTraceLevel;
            Environment.SetEnvironmentVariable("STRANSFER_STATE_DIR", previousState);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(result == 0 ? "\nWPF smoke tests passed." : "\nWPF smoke tests failed.");
        return result;
    }

    private static T Find<T>(MainWindow window, string name) where T : FrameworkElement =>
        Descendants<T>(window).FirstOrDefault(item => item.Name == name) ?? throw new InvalidOperationException($"Missing XAML element: {name}");

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void TestSingleInstance(List<Exception> errors)
    {
        var channel = Guid.NewGuid().ToString("N");
        using var server = new Stransfer.App.Services.SingleInstance(channel);
        using var client = new Stransfer.App.Services.SingleInstance(channel);
        Require(server.IsFirst && !client.IsFirst, "Single-instance ownership was not enforced.");
        string[]? received = null;
        var listen = server.ListenAsync(arguments => received = arguments);
        var expected = new[] { "--paste", "--destination", @"C:\Transfer test\folder with spaces\" };
        var send = client.SendAsync(expected);
        PumpUntil(() => send.IsCompleted && received is not null, errors, "Single-instance IPC did not deliver its arguments.");
        send.GetAwaiter().GetResult();
        Require(received!.SequenceEqual(expected), "Single-instance IPC changed an argument.");
        server.Dispose();
        PumpUntil(() => listen.IsCompleted, errors, "Single-instance listener did not stop.");
        listen.GetAwaiter().GetResult();
        Console.WriteLine("PASS  current-user single-instance IPC preserves quoted paths and shuts down cleanly");
    }
    private static void TestCompletedRemoval(string root, List<Exception> errors)
    {
        var state = Path.Combine(root, "history-removal");
        var source = Path.Combine(root, "source", "history.txt"); File.WriteAllText(source, "Keep completed contents.");
        var destination = Path.Combine(root, "history-output");
        Guid id;
        using (var seed = new TransferManager(state))
        {
            id = seed.CreateJobAsync([source], destination).GetAwaiter().GetResult().Id;
            seed.RunAsync(id).GetAwaiter().GetResult();
            Require(seed.Jobs.Single().Status == TransferStatus.Completed, "History removal fixture did not finish.");
        }
        var path = Path.Combine(state, id.ToString("N") + ".json");
        var record = JsonNode.Parse(File.ReadAllText(path))!;
        record["Volumes"]![0]!["Identity"] = "volume:disconnected-test-device";
        File.WriteAllText(path, record.ToJsonString());
        using var coordinator = new Stransfer.App.Services.TransferCoordinator(state);
        Require(!coordinator.Manager.AreDrivesAvailable(id), "The history test did not simulate a disconnected drive.");
        var remove = coordinator.RemoveAsync(id);
        PumpUntil(() => remove.IsCompleted, errors, "Completed history could not be removed without its drive.");
        remove.GetAwaiter().GetResult();
        Require(coordinator.Jobs.Count == 0 && File.Exists(source) && File.Exists(Path.Combine(destination, "history.txt")),
            "Removing completed history changed a source or destination.");
        Console.WriteLine("PASS  completed history removal works with the original drive absent and retains all contents");
    }
    private static void SavePreview(MainWindow window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("STRANSFER_PREVIEW_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void PumpUntil(Func<bool> condition, List<Exception> errors, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (errors.Count > 0) throw new AggregateException("WPF dispatcher error.", errors);
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(failure);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        if (errors.Count > 0) throw new AggregateException("WPF dispatcher error.", errors);
    }

    private static void AssertNoErrors(List<Exception> errors, BindingErrorListener listener)
    {
        if (errors.Count > 0) throw new AggregateException("WPF dispatcher error.", errors);
        Require(string.IsNullOrWhiteSpace(listener.Output), "WPF binding trace errors:\n" + listener.Output);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class BindingErrorListener : TraceListener
{
    private readonly StringBuilder output = new();
    public string Output => output.ToString();
    public override void Write(string? message) => output.Append(message);
    public override void WriteLine(string? message) => output.AppendLine(message);
}
