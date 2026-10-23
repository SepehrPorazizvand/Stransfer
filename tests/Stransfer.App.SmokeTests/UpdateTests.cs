using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stransfer.App.Services;

namespace Stransfer.App.SmokeTests;

internal static class UpdateTests
{
    internal static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "updates-test"); Directory.CreateDirectory(folder);
        var packageName = "Stransfer-portable-win-x64.zip";
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            draft = false, prerelease = false, tag_name = "v9.0.0",
            assets = new[] { new { id = 1, name = packageName, size = 123L }, new { id = 2, name = "update-win-x64.json", size = 400L } }
        });
        using var handler = new MetadataHandler(metadata);
        using (var client = new GitHubUpdates(handler))
        {
            var found = await client.CheckAsync();
            Require(found?.Version == new Version(9, 0, 0), "New release was not found.");
            Require(handler.Calls == 1, "Checking downloaded more than release metadata.");
        }
        Require(GitHubUpdates.ParseRelease(metadata, new Version(9, 0, 0, 0), "win-x64") is null, "Equal versions must not update.");
        Require(GitHubUpdates.ParseRelease(metadata, new Version(10, 0, 0), "win-x64") is null, "Downgrades must not update.");
        var prerelease = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(metadata).Replace("\"prerelease\":false", "\"prerelease\":true"));
        Require(GitHubUpdates.ParseRelease(prerelease, new Version(1, 0, 0), "win-x64") is null, "Prereleases must not auto-update.");

        var bytes = Encoding.UTF8.GetBytes("new application bytes");
        var package = Path.Combine(folder, "good.zip");
        CreateZip(package, "Stransfer/Stransfer.exe", bytes);
        var manifest = new UpdateManifest(1, "9.0.0", packageName, new FileInfo(package).Length, Hash(File.ReadAllBytes(package)), bytes.Length, Hash(bytes));
        var release = new AvailableUpdate(new Version(9, 0, 0), new(1, packageName, manifest.PackageSize), new(2, "update-win-x64.json", 400));
        GitHubUpdates.ValidateManifest(manifest, release);
        Throws(() => GitHubUpdates.ValidateManifest(manifest with { Version = "8.0.0" }, release));
        Throws(() => GitHubUpdates.ValidateManifest(manifest with { Sha256 = "invalid" }, release));
        var extracted = Path.Combine(folder, "verified.exe");
        await GitHubUpdates.ExtractVerifiedAsync(package, extracted, manifest, CancellationToken.None);
        Require(File.ReadAllBytes(extracted).SequenceEqual(bytes), "Verified extraction changed bytes.");
        await ThrowsAsync(() => GitHubUpdates.ExtractVerifiedAsync(package, Path.Combine(folder, "bad-hash.exe"), manifest with { Sha256 = new string('0', 64) }, CancellationToken.None));
        Require(!File.Exists(Path.Combine(folder, "bad-hash.exe")), "Invalid package was extracted.");
        var unsafeZip = Path.Combine(folder, "unsafe.zip");
        CreateZip(unsafeZip, "../escape.exe", bytes);
        await ThrowsAsync(() => GitHubUpdates.ExtractVerifiedAsync(unsafeZip, Path.Combine(folder, "unsafe.exe"), manifest with { Sha256 = Hash(File.ReadAllBytes(unsafeZip)) }, CancellationToken.None));
        Require(!File.Exists(Path.Combine(root, "escape.exe")), "ZIP traversal escaped staging.");

        var target = Path.Combine(folder, "Stransfer.exe");
        File.WriteAllText(target, "old application");
        var settings = Path.Combine(folder, "settings.json"); File.WriteAllText(settings, "settings stay");
        var backup = await UpdateInstaller.ReplaceAsync(extracted, target, Guid.NewGuid(), Hash(bytes));
        Require(File.ReadAllBytes(target).SequenceEqual(bytes) && File.ReadAllText(backup) == "old application", "Update swap did not retain a rollback copy.");
        UpdateInstaller.Restore(target, backup);
        Require(File.ReadAllText(target) == "old application" && File.ReadAllText(settings) == "settings stay", "Rollback changed the original app or settings.");
        await ThrowsAsync(() => UpdateInstaller.ReplaceAsync(extracted, target, Guid.NewGuid(), new string('0', 64)));
        Require(File.ReadAllText(target) == "old application", "Failed verification replaced the current app.");
        Require(!Directory.EnumerateFiles(folder, ".stransfer-*").Any(), "Updater left replacement files in the application folder.");
        Console.WriteLine("PASS  metadata-only update checks, version gates, manifests, SHA-256, ZIP traversal refusal, replacement and rollback with settings retained");
    }

    private static void CreateZip(string path, string name, byte[] bytes)
    { using var archive = ZipFile.Open(path, ZipArchiveMode.Create); using var output = archive.CreateEntry(name).Open(); output.Write(bytes); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid update was accepted."); }
    private static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid update was accepted."); }
    private sealed class MetadataHandler(byte[] response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Require(request.RequestUri?.AbsolutePath == "/repos/SepehrPorazizvand/Stransfer/releases/latest", "A check requested a download.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) });
        }
    }
}
