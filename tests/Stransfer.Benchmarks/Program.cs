using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Stransfer.Core;

var reportPath = args.FirstOrDefault(argument => !argument.StartsWith("--"));
bool verify = args.Contains("--verify");
var root = Path.Combine(Path.GetTempPath(), "Stransfer-benchmark", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<object>();
try
{
    foreach (var workload in new[] { (Name: "1000 × 1 KiB", Count: 1000, Size: 1024), (Name: "200 × 64 KiB", Count: 200, Size: 64 * 1024), (Name: "1 × 128 MiB", Count: 1, Size: 128 * 1024 * 1024) })
    {
        var source = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"), "source")).FullName;
        var block = new byte[Math.Min(workload.Size, 1024 * 1024)]; Random.Shared.NextBytes(block);
        for (int index = 0; index < workload.Count; index++)
        {
            using var stream = File.Create(Path.Combine(source, $"{index:D6}.bin"));
            for (int bytes = 0; bytes < workload.Size; bytes += block.Length) stream.Write(block.AsSpan(0, Math.Min(block.Length, workload.Size - bytes)));
            stream.Flush(true);
        }
        var samples = new Dictionary<string, List<double>> { ["Windows File.Copy + flush"] = [], ["Stransfer 1 worker"] = [], ["Stransfer 4 workers"] = [] };
        // Rotate order to reduce the effect of always granting one engine a warmer cache.
        for (int round = 0; round < 3; round++)
        {
            foreach (var mode in Enumerable.Range(0, 3).Select(index => (index + round) % 3))
            {
                var name = samples.Keys.ElementAt(mode);
                var destination = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
                var watch = Stopwatch.StartNew();
                if (mode == 0)
                {
                    foreach (var file in Directory.EnumerateFiles(source))
                    {
                        var target = Path.Combine(destination, Path.GetFileName(file)); File.Copy(file, target);
                        using (var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
                        if (verify)
                        {
                            await using var input = File.OpenRead(file); await using var output = File.OpenRead(target);
                            if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(output))) throw new IOException("Baseline verification failed.");
                        }
                    }
                }
                else
                {
                    using var manager = new TransferManager(Path.Combine(root, Guid.NewGuid().ToString("N"), "state"));
                    var job = await manager.CreateJobAsync([source], destination, verify: verify, options: new TransferOptions { SmallFileWorkers = mode == 1 ? 1 : 4 });
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
    var report = new { timestamp = DateTimeOffset.UtcNow, os = Environment.OSVersion.ToString(), verification = verify, rounds = 3,
        methodology = "Local temporary directory; warm-cache process-level benchmark. Includes scan, preflight and journal for Stransfer. Native baseline is File.Copy followed by Flush(true), not the Explorer UI. No cold-cache or physical-drive claims.", results };
    if (reportPath is not null) await File.WriteAllTextAsync(Path.GetFullPath(reportPath), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(root, true); }
