using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stransfer.App;
using Stransfer.App.Services;
using Stransfer.App.Views;
using Stransfer.Core;

namespace Stransfer.App.SmokeTests;

internal static class CompactTransferTests
{
    public static void Run(MainWindow main, string root)
    {
        var source = Path.Combine(root, "compact.bin");
        using (var stream = File.Create(source)) stream.SetLength(96L * 1024 * 1024);
        var destination = Path.Combine(root, "compact-output"); Directory.CreateDirectory(destination);
        var runtime = main.Runtime;
        var previousVerification = main.ViewModel.Settings.VerifyCopies;
        main.ViewModel.Settings.VerifyCopies = true; // The streaming path checkpoints this 96 MiB UI fixture at 32 MiB.
        using var released = new ManualResetEventSlim();
        int blocked = 0;
        EventHandler<TransferJobSnapshot> hold = (_, snapshot) =>
        {
            if (snapshot.Sources.Contains(source) && snapshot.Status == TransferStatus.Running && snapshot.BytesTransferred >= 32L * 1024 * 1024
                && Interlocked.Exchange(ref blocked, 1) == 0) released.Wait(TimeSpan.FromSeconds(10));
        };
        runtime.Coordinator.Manager.JobChanged += hold;
        try
        {
            main.Hide();
            var paste = runtime.AcceptPasteAsync(new([source], TransferOperation.Copy, 0), destination);
            var compact = runtime.TransferWindows.Single();
            compact.UpdateLayout(); Save(compact, "compact-preparing.png");
            Pump(() => paste.IsCompleted && compact.ViewModel.HasJob && Volatile.Read(ref blocked) == 1);
            paste.GetAwaiter().GetResult();
            Require(!main.IsVisible && compact.IsVisible && compact.Width == 480 && compact.Height == 330,
                "Shortcut paste opened the dashboard or used the wrong compact dimensions.");
            Require(compact.ViewModel.StatusDetail.Contains("Verified"), "Compact progress hid the job's actual verification mode.");
            var id = compact.ViewModel.JobId!.Value;
            Pump(() => compact.ViewModel.Job.CanPause);
            compact.ViewModel.Pause.Execute(null); released.Set();
            Pump(() => compact.ViewModel.Job.IsPaused && compact.ViewModel.Job.CanResume);
            Require(!main.IsVisible, "Pausing a compact transfer revealed the dashboard.");
            compact.UpdateLayout(); Save(compact, "compact-paused.png");
            var bar = (ProgressBar)compact.FindName("TransferProgress");
            Require(bar.ActualWidth > 300 && bar.Value > 0 && bar.Value < 100 && compact.IsArrangeValid,
                "Compact progress was not bound/measured at the saved checkpoint.");
            compact.ViewModel.Resume.Execute(null);
            compact.Close();
            Require(runtime.TransferWindows.Count == 0 && !main.IsVisible, "Closing progress revealed the dashboard or leaked a window.");
            Pump(() => runtime.Coordinator.Jobs.Single(job => job.Id == id).Status == TransferStatus.Completed);
            main.ViewModel.Settings.VerifyCopies = false;
            Require(new FileInfo(Path.Combine(destination, "compact.bin")).Length == 96L * 1024 * 1024,
                "Closing the compact window canceled the resumed transfer.");

            var tiny = Path.Combine(root, "compact-small.txt"); File.WriteAllText(tiny, "Compact shortcut transfer.");
            var second = runtime.AcceptPasteAsync(new([tiny], TransferOperation.Copy, 0), destination);
            Pump(() => second.IsCompleted); second.GetAwaiter().GetResult();
            var completed = runtime.TransferWindows.Single();
            Pump(() => completed.ViewModel.Job.IsCompleted);
            Require(completed.ViewModel.StatusDetail.Contains("Fast"), "Fast jobs were mislabeled in compact progress.");
            Require(!main.IsVisible && completed.ViewModel.Job.Percent == 100, "Completion opened the dashboard or lost progress.");
            completed.UpdateLayout(); Save(completed, "compact-completed.png"); completed.Close();

            var failure = runtime.AcceptPasteAsync(new([Path.Combine(root, "missing-compact.txt")], TransferOperation.Copy, 0), destination);
            Pump(() => failure.IsCompleted); failure.GetAwaiter().GetResult();
            var failed = runtime.TransferWindows.Single();
            Require(!main.IsVisible && failed.ViewModel.HasMessage && !failed.ViewModel.IsPreparing && !failed.ViewModel.HasJob,
                "Preflight error opened the dashboard or left a loading spinner.");
            failed.UpdateLayout(); Save(failed, "compact-error.png"); failed.Close();
            Console.WriteLine("PASS  compact shortcut progress, hidden dashboard, 480x330 layout, Pause/Resume, close-and-continue, completion and inline preflight errors");
        }
        finally
        {
            released.Set(); runtime.Coordinator.Manager.JobChanged -= hold;
            main.ViewModel.Settings.VerifyCopies = previousVerification;
            foreach (var compact in runtime.TransferWindows.ToArray()) compact.Close();
        }
    }
    private static void Pump(Func<bool> done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Compact transfer UI did not reach its expected state.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
    }
    private static void Save(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("STRANSFER_PREVIEW_DIR"); if (directory is null) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
