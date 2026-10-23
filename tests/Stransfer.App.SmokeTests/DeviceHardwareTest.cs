using System.IO;
using Stransfer.App.Services;
using Stransfer.Core;

namespace Stransfer.App.SmokeTests;

// Explicit opt-in hardware check. It creates one unique folder, never operates on existing phone data.
internal static class DeviceHardwareTest
{
    public static int Run(bool cleanup = false)
    {
        try { return (cleanup ? CleanupAsync() : CheckAsync()).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.WriteLine("FAIL physical device: " + error); return 1; }
    }
    private static Task<string> DeviceAsync()
    {
        return NamespaceCom.Sta(() =>
        {
            object? shell = null, pc = null, entries = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                pc = ((dynamic)shell!).Namespace(17); entries = ((dynamic)pc).Items();
                var paths = new List<string>();
                for (int i = 0; i < (int)((dynamic)entries).Count; i++)
                {
                    object item = ((dynamic)entries).Item(i);
                    try { string path = ((dynamic)item).Path; if (path.StartsWith("::{", StringComparison.Ordinal)) paths.Add(path); }
                    finally { NamespaceCom.Release(item); }
                }
                if (paths.Count != 1) throw new IOException("Connect exactly one unlocked MTP device for the explicit hardware check.");
                using var storage = new TransferStorage();
                var volumes = storage.Children(TransferStorage.Encode(paths[0])).Where(entry => entry.Directory).ToArray();
                if (volumes.Length != 1) throw new IOException("The hardware check requires exactly one exposed storage volume.");
                var capacity = storage.Capacity(volumes[0].Location);
                Console.WriteLine($"Device storage found; {capacity.FreeBytes} bytes free.");
                return volumes[0].Location;
            }
            finally { NamespaceCom.Release(entries); NamespaceCom.Release(pc); NamespaceCom.Release(shell); }
        });
    }
    private static async Task<int> CheckAsync()
    {
        var device = await DeviceAsync();
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "Stransfer-hardware-" + id);
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Stransfer-test-" + id); Directory.CreateDirectory(source);
        var content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(source, "roundtrip.bin"), content); Directory.CreateDirectory(Path.Combine(source, "empty"));
        using var manager = new NamespaceTransferManager(Path.Combine(root, "jobs"));
        var outbound = await manager.CreateAsync([source], device, TransferOperation.Copy, true);
        await manager.RunAsync(outbound.Id, CancellationToken.None); Done(manager, outbound.Id);
        var testFolder = await NamespaceCom.Sta(() => { using var storage = new TransferStorage(); return storage.Find(device, Path.GetFileName(source))!.Location; });
        var destination = Path.Combine(root, "roundtrip"); Directory.CreateDirectory(destination);
        var inbound = await manager.CreateAsync([testFolder], destination, TransferOperation.Copy, true);
        using (var stop = new CancellationTokenSource())
        {
            manager.JobChanged += (_, job) => { if (job.Id == inbound.Id && job.BytesTransferred >= 32L * 1024 * 1024) stop.Cancel(); };
            await manager.RunAsync(inbound.Id, stop.Token);
            if (manager.Jobs.Single(job => job.Id == inbound.Id).Status != TransferStatus.Paused) throw new IOException("Physical byte-resume test did not pause.");
        }
        using (var recovered = new NamespaceTransferManager(Path.Combine(root, "jobs")))
        {
            if (recovered.RecoveryWarnings.Count != 0) throw new IOException(string.Join("; ", recovered.RecoveryWarnings));
            await recovered.RunAsync(inbound.Id, CancellationToken.None); Done(recovered, inbound.Id);
        }
        if (!File.ReadAllBytes(Path.Combine(destination, Path.GetFileName(source), "roundtrip.bin")).SequenceEqual(content)) throw new IOException("Hardware round-trip differs.");
        await NamespaceCom.Sta(() =>
        {
            using var storage = new TransferStorage();
            // Only the unique fixture created above is removed, after verified round-trip.
            var children = storage.Children(testFolder);
            if (children.Count != 2 || !children.Any(entry => entry.Name == "roundtrip.bin") || !children.Any(entry => entry.Name == "empty"))
                throw new IOException("Fixture changed; automatic cleanup skipped.");
            foreach (var child in children) storage.Delete(child.Location);
            storage.Delete(testFolder); return true;
        });
        Console.WriteLine("PASS physical MTP PC -> phone -> PC; 64 MiB, pause at 32 MiB, restart/resume, verified contents, empty folder and unique fixture cleanup. Local evidence: " + root);
        return 0;
    }
    private static async Task<int> CleanupAsync()
    {
        var device = await DeviceAsync();
        return await NamespaceCom.Sta(() =>
        {
            using var storage = new TransferStorage(); int removed = 0;
            foreach (var root in Directory.EnumerateDirectories(Path.GetTempPath(), "Stransfer-hardware-*"))
            {
                var suffix = Path.GetFileName(root)["Stransfer-hardware-".Length..];
                if (!Guid.TryParseExact(suffix, "N", out _)) continue;
                var name = "Stransfer-test-" + suffix; var source = Path.Combine(root, name, "roundtrip.bin");
                if (!File.Exists(source) || !Directory.Exists(Path.Combine(root, "jobs"))) continue;
                var records = Directory.EnumerateFiles(Path.Combine(root, "jobs"), "*.json")
                    .Select(path => System.Text.Json.JsonSerializer.Deserialize<NamespaceJob>(File.ReadAllBytes(path)))
                    .Where(job => job is not null && job.Sources.SequenceEqual(new[] { Path.Combine(root, name) })).ToArray();
                if (records.Length != 1) continue;
                var fixture = storage.Find(device, name); if (fixture is null || !fixture.Directory) continue;
                var children = storage.Children(fixture.Location);
                var partialName = $".stransfer-{records[0]!.Id:N}-0.partial";
                if (children.Any(entry => entry.Directory ? entry.Name != "empty" || storage.Children(entry.Location).Count != 0
                    : entry.Name != partialName && entry.Name != "roundtrip.bin")) continue;
                var expected = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));
                bool matches = true;
                foreach (var child in children.Where(entry => !entry.Directory))
                {
                    using var input = storage.Read(child.Location);
                    if (!System.Security.Cryptography.SHA256.HashData(input).SequenceEqual(expected)) { matches = false; break; }
                }
                if (!matches) continue;
                foreach (var child in children) storage.Delete(child.Location);
                storage.Delete(fixture.Location); removed++;
            }
            Console.WriteLine($"Removed {removed} verified, uniquely owned earlier hardware-test folders."); return 0;
        });
    }
    private static void Done(NamespaceTransferManager manager, Guid id)
    {
        var job = manager.Jobs.Single(job => job.Id == id);
        if (job.Status != TransferStatus.Completed) throw new IOException(job.Error, manager.LastFailure);
    }
}
