using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Stransfer.Core;

var reportPath = args.FirstOrDefault(argument => !argument.StartsWith("--"));
bool verify = args.Contains("--verify");
bool compareVerification = args.Contains("--compare-verification");
string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
if (Option("--source") is { } realSource)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The real-folder comparison uses the Windows Shell copy engine.");
    await CameraBenchmark.RunAsync(realSource, Option("--destination-root") ?? throw new ArgumentException("Specify --destination-root."),
        reportPath ?? throw new ArgumentException("Specify a report path as the first argument."));
    return;
}
var root = Path.Combine(Path.GetTempPath(), "Stransfer-benchmark", Guid.NewGuid().ToString("N"));
var destinationRoot = Path.Combine(Option("--destination-root") ?? root, "Stransfer-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Directory.CreateDirectory(destinationRoot);
var results = new List<object>();
try
{
    foreach (var workload in new[] { (Name: "1000 × 1 KiB", Count: 1000, Size: 1024), (Name: "200 × 64 KiB", Count: 200, Size: 64 * 1024), (Name: "1 × 512 MiB", Count: 1, Size: 512 * 1024 * 1024) }
        .Where(workload => !args.Contains("--large-only") || workload.Count == 1))
    {
        var source = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"), "source")).FullName;
        var block = new byte[Math.Min(workload.Size, 1024 * 1024)]; Random.Shared.NextBytes(block);
        for (int index = 0; index < workload.Count; index++)
        {
            using var stream = File.Create(Path.Combine(source, $"{index:D6}.bin"));
            for (int bytes = 0; bytes < workload.Size; bytes += block.Length) stream.Write(block.AsSpan(0, Math.Min(block.Length, workload.Size - bytes)));
            stream.Flush(true);
        }
        var samples = new Dictionary<string, List<double>> { ["Windows File.Copy + flush"] = [],
            [compareVerification ? "Stransfer verification off" : "Stransfer 1 worker"] = [],
            [compareVerification ? "Stransfer verification on" : "Stransfer 4 workers"] = [] };
        // Rotate order to reduce the effect of always granting one engine a warmer cache.
        for (int round = 0; round < 3; round++)
        {
            foreach (var mode in Enumerable.Range(0, 3).Select(index => (index + round) % 3))
            {
                var name = samples.Keys.ElementAt(mode);
                var destination = Directory.CreateDirectory(Path.Combine(destinationRoot, Guid.NewGuid().ToString("N"))).FullName;
                var watch = Stopwatch.StartNew();
                if (mode == 0)
                {
                    foreach (var file in Directory.EnumerateFiles(source))
                    {
                        var target = Path.Combine(destination, Path.GetFileName(file)); File.Copy(file, target);
                        using (var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
                        if (verify && !compareVerification)
                        {
                            await using var input = File.OpenRead(file); await using var output = File.OpenRead(target);
                            if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(output))) throw new IOException("Baseline verification failed.");
                        }
                    }
                }
                else
                {
                    using var manager = new TransferManager(Path.Combine(root, Guid.NewGuid().ToString("N"), "state"));
                    var job = await manager.CreateJobAsync([source], destination, verify: compareVerification ? mode == 2 : verify,
                        options: new TransferOptions { SmallFileWorkers = compareVerification ? 0 : mode == 1 ? 1 : 4 });
                    await manager.RunAsync(job.Id);
                    if (manager.Jobs.Single().Status != TransferStatus.Completed) throw new IOException(manager.Jobs.Single().Error);
                }
                watch.Stop(); samples[name].Add(watch.Elapsed.TotalSeconds);
                Directory.Delete(destination, true);
            }
        }
        foreach (var pair in samples)
        {
            double seconds = pair.Value.Order().ElementAt(1);
            Console.WriteLine($"{workload.Name,-18} {pair.Key,-28} {seconds,7:0.000} s");
            results.Add(new { workload = workload.Name, engine = pair.Key, medianSeconds = seconds, samplesSeconds = pair.Value });
        }
    }
    var report = new { timestamp = DateTimeOffset.UtcNow, os = Environment.OSVersion.ToString(), verification = verify, compareVerification, sourceDrive = Path.GetPathRoot(root), destinationDrive = Path.GetPathRoot(destinationRoot), rounds = 3,
        methodology = "Warm-cache process-level benchmark, rotated order. Includes scan, preflight and journal for Stransfer. Native baseline is File.Copy followed by Flush(true), not Explorer UI. Comparison mode uses no baseline hashing and reports Stransfer verification on/off separately. No cold-cache claims.", results };
    if (reportPath is not null) await File.WriteAllTextAsync(Path.GetFullPath(reportPath), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(destinationRoot, true); Directory.Delete(root, true); }
