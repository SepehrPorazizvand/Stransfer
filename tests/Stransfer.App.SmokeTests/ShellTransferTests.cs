using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using Stransfer.App.Services;
using Stransfer.Core;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Stransfer.App.SmokeTests;

internal static class ShellTransferTests
{
    public static async Task RunAsync(string root)
    {
        var source = Path.Combine(root, "shell-source", "device-folder");
        var destination = Path.Combine(root, "shell-copy");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "device-file.txt"), "Namespace object contents.");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var item = NamespaceCom.Open(source);
        ComDataObject? data = null;
        try
        {
            data = NamespaceCom.Bind<ComDataObject>(item, new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4"));
            var selection = new NamespaceSelection(data);
            Require(ShellClipboardReader.HasShellItems(selection), "Shell selection was not recognized.");
            using var transfer = new ShellClipboardTransfer(selection);
            var paths = transfer.ReadSources();
            Require(paths.Length == 1 && TransferStorage.IsShell(paths[0]), "Selection did not become a durable namespace locator.");
            using var manager = new NamespaceTransferManager(Path.Combine(root, "namespace-jobs"));
            var first = await manager.CreateAsync(paths, destination, TransferOperation.Copy, true);
            await manager.RunAsync(first.Id, CancellationToken.None);
            Done(manager, first.Id);
            Require(File.ReadAllText(Path.Combine(destination, "device-folder", "device-file.txt")) == "Namespace object contents."
                && Directory.Exists(Path.Combine(destination, "device-folder", "empty")), "Streamed folder lost data or empty directories.");
            // Simulate loss after rename but before the committed/completed records reach disk.
            var journal = Path.Combine(root, "namespace-jobs", first.Id.ToString("N") + ".journal");
            var lines = File.ReadAllLines(journal);
            var ready = Array.FindIndex(lines, line =>
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(line);
                var file = parsed.RootElement.GetProperty("File");
                return file.ValueKind == System.Text.Json.JsonValueKind.Object && file.GetProperty("Ready").GetBoolean() && !file.GetProperty("Committed").GetBoolean();
            });
            Require(ready >= 0, "No durable publish intent was recorded.");
            File.WriteAllLines(journal, lines.Take(ready + 1));
            using (var recovery = new NamespaceTransferManager(Path.Combine(root, "namespace-jobs")))
            {
                await recovery.RemoveAsync(first.Id);
                Require(File.Exists(Path.Combine(destination, "device-folder", "device-file.txt")), "Removing interrupted history deleted a published file.");
            }
            var second = await manager.CreateAsync(paths, destination, TransferOperation.Copy, true);
            await manager.RunAsync(second.Id, CancellationToken.None); Done(manager, second.Id);
            Require(Directory.Exists(Path.Combine(destination, "device-folder (2)")) && Directory.Exists(source), "Keep-both or source retention failed.");
            Console.WriteLine("PASS  durable Shell selection, own IStream engine, SHA-256 verification, empty directories and keep-both");
            Console.WriteLine("PASS  history removal after interrupted publish retains the completed destination");
            await OutboundAsync(root, source, manager);
            await ResumeAsync(root, manager);
        }
        finally { NamespaceCom.Release(data); NamespaceCom.Release(item); }
    }
    private static async Task OutboundAsync(string root, string source, NamespaceTransferManager manager)
    {
        var folder = Path.Combine(root, "shell-destination"); Directory.CreateDirectory(folder);
        var job = await manager.CreateAsync([source], TransferStorage.Encode(folder), TransferOperation.Copy, true);
        await manager.RunAsync(job.Id, CancellationToken.None); Done(manager, job.Id);
        Require(File.ReadAllText(Path.Combine(folder, "device-folder", "device-file.txt")) == "Namespace object contents.", "Shell destination contents differ.");
        var phoneToPhone = Path.Combine(root, "shell-to-shell"); Directory.CreateDirectory(phoneToPhone);
        var cross = await manager.CreateAsync([TransferStorage.Encode(source)], TransferStorage.Encode(phoneToPhone), TransferOperation.Copy, true);
        await manager.RunAsync(cross.Id, CancellationToken.None); Done(manager, cross.Id);
        Console.WriteLine("PASS  PC to Shell and Shell to Shell direct streaming with read-back verification and commit rename");
    }
    private static async Task ResumeAsync(string root, NamespaceTransferManager manager)
    {
        var source = Path.Combine(root, "large-device.bin");
        using (var file = File.Create(source)) { var block = RandomNumberGenerator.GetBytes(1024 * 1024); for (int i = 0; i < 72; i++) file.Write(block); }
        var destination = Path.Combine(root, "resume-device"); Directory.CreateDirectory(destination);
        var job = await manager.CreateAsync([TransferStorage.Encode(source)], destination, TransferOperation.Copy, true);
        using var stop = new CancellationTokenSource();
        EventHandler<TransferJobSnapshot> cancel = (_, value) => { if (value.Id == job.Id && value.BytesTransferred >= 32L * 1024 * 1024) stop.Cancel(); };
        manager.JobChanged += cancel;
        await manager.RunAsync(job.Id, stop.Token); manager.JobChanged -= cancel;
        var paused = manager.Jobs.Single(value => value.Id == job.Id);
        Require(paused.Status == TransferStatus.Paused && paused.BytesTransferred is > 0 and < 75497472, "Device byte checkpoint was not paused.");
        using var recovered = new NamespaceTransferManager(Path.Combine(root, "namespace-jobs"));
        Require(recovered.RecoveryWarnings.Count == 0 && recovered.Jobs.Single(value => value.Id == job.Id).BytesTransferred == paused.BytesTransferred, "Restart lost saved device progress.");
        var partial = Directory.GetFiles(destination, ".stransfer-*.partial").Single();
        byte original; using (var file = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite)) { original = (byte)file.ReadByte(); file.Position = 0; file.WriteByte((byte)(original ^ 255)); }
        await recovered.RunAsync(job.Id, CancellationToken.None);
        Require(recovered.Jobs.Single(value => value.Id == job.Id).Status == TransferStatus.Failed && File.Exists(source), "Corrupt resume prefix was trusted.");
        using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write)) file.WriteByte(original);
        await recovered.RunAsync(job.Id, CancellationToken.None); Done(recovered, job.Id);
        Require(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, Path.GetFileName(source))))), "Resumed stream differs.");
        Console.WriteLine("PASS  device pause at durable byte checkpoint, process restart, corrupt-prefix refusal and verified resume");
    }
    private static void Done(NamespaceTransferManager manager, Guid id)
    {
        var job = manager.Jobs.Single(value => value.Id == id);
        Require(job.Status == TransferStatus.Completed, "Namespace transfer failed: " + job.Error + "\n" + manager.LastFailure);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
// Exposes the namespace format used by portable devices, with ordinary filesystem paths deliberately unavailable.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NamespaceSelection(ComDataObject inner) : ComDataObject
{
    public int QueryGetData(ref FORMATETC format) => format.cfFormat == 15 ? unchecked((int)0x80040064) : inner.QueryGetData(ref format);
    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (format.cfFormat == 15) throw new COMException("FileDrop unavailable", unchecked((int)0x80040064));
        inner.GetData(ref format, out medium);
    }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => inner.GetDataHere(ref format, ref medium);
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) => inner.GetCanonicalFormatEtc(ref input, out output);
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => inner.SetData(ref format, ref medium, release);
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) => inner.DAdvise(ref format, flags, sink, out connection);
    public void DUnadvise(int connection) => inner.DUnadvise(connection);
    public int EnumDAdvise(out IEnumSTATDATA enumerator) { var result = inner.EnumDAdvise(out var value); enumerator = value!; return result; }
}
