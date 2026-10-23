using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stransfer.App.Services;

public sealed record UpdateAsset(long Id, string Name, long Size);
public sealed record AvailableUpdate(Version Version, UpdateAsset Package, UpdateAsset Manifest);
public sealed record UpdateManifest(int SchemaVersion, string Version, string AssetName, long PackageSize,
    string Sha256, long ExecutableSize, string ExecutableSha256);

public sealed class GitHubUpdates : IDisposable
{
    public const string Repository = "SepehrPorazizvand/Stransfer";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases/latest";
    private const long MaximumPackage = 1024L * 1024 * 1024;
    private readonly HttpClient client;
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static Version CurrentVersion => typeof(GitHubUpdates).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static string DisplayVersion => CurrentVersion.ToString(3);
    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    { Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => throw new PlatformNotSupportedException("Updates require Windows x64 or ARM64.") };

    public GitHubUpdates(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Stransfer/" + DisplayVersion);
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IOException("No public release is available yet. Try again later.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new IOException("GitHub's request limit was reached. Try again later.");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response, 512 * 1024, timeout.Token);
        return ParseRelease(bytes, CurrentVersion, ArchitectureName);
    }

    internal static AvailableUpdate? ParseRelease(byte[] bytes, Version current, string architecture)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Build < 0)
            throw new InvalidDataException("The release version is invalid.");
        if (Normalize(version) <= Normalize(current)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().Select(asset => new UpdateAsset(
            asset.GetProperty("id").GetInt64(), asset.GetProperty("name").GetString() ?? "", asset.GetProperty("size").GetInt64())).ToArray();
        UpdateAsset Find(string name, long max) => assets.SingleOrDefault(asset => asset.Name == name && asset.Id > 0 && asset.Size > 0 && asset.Size <= max)
            ?? throw new InvalidDataException("The release is missing a valid " + name + ".");
        return new(version, Find("Stransfer-portable-" + architecture + ".zip", MaximumPackage), Find("update-" + architecture + ".json", 16384));
    }

    internal static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public async Task<UpdatePlan> DownloadAsync(AvailableUpdate release, string stateDirectory, string target,
        bool restartBackground, IProgress<double>? progress, CancellationToken token)
    {
        UpdateInstaller.CheckTarget(target);
        using var metadataResponse = await GetAssetAsync(release.Manifest, token);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await ReadBoundedAsync(metadataResponse, 16384, token), JsonOptions)
            ?? throw new InvalidDataException("The update manifest is empty.");
        ValidateManifest(manifest, release);
        var id = Guid.NewGuid();
        var folder = UpdateInstaller.StageDirectory(stateDirectory, id);
        Directory.CreateDirectory(folder);
        var packagePath = Path.Combine(folder, "package.zip");
        try
        {
            CheckSpace(folder, checked(manifest.PackageSize + manifest.ExecutableSize + 64L * 1024 * 1024));
            CheckSpace(Path.GetDirectoryName(target)!, checked(manifest.ExecutableSize + 64L * 1024 * 1024));
            using (var response = await GetAssetAsync(release.Package, token))
            {
                if (response.Content.Headers.ContentLength is long length && length != manifest.PackageSize)
                    throw new InvalidDataException("The update download size changed. Check for updates again.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                var buffer = new byte[1024 * 1024]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    total = checked(total + read);
                    if (total > manifest.PackageSize) throw new InvalidDataException("The update download exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report(100.0 * total / manifest.PackageSize);
                }
                if (total != manifest.PackageSize) throw new IOException("The update download was interrupted. Try again.");
                await output.FlushAsync(token);
            }
            var executable = Path.Combine(folder, "Stransfer.exe");
            await ExtractVerifiedAsync(packagePath, executable, manifest, token);
            ValidateExecutable(executable, release.Version, ArchitectureName);
            using var current = Process.GetCurrentProcess();
            var plan = new UpdatePlan(id, Path.GetFullPath(target), current.Id, current.StartTime.ToUniversalTime().Ticks,
                manifest.ExecutableSha256, release.Version.ToString(3), restartBackground, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Combine(folder, "plan.json"), JsonSerializer.Serialize(plan, JsonOptions), token);
            return plan;
        }
        catch { UpdateInstaller.DeleteStage(stateDirectory, id); throw; }
    }

    internal static void ValidateManifest(UpdateManifest manifest, AvailableUpdate release)
    {
        if (manifest.SchemaVersion != 1 || !Version.TryParse(manifest.Version, out var version)
            || Normalize(version) != Normalize(release.Version) || manifest.AssetName != release.Package.Name
            || manifest.PackageSize != release.Package.Size || manifest.PackageSize <= 0 || manifest.PackageSize > MaximumPackage
            || manifest.ExecutableSize <= 0 || manifest.ExecutableSize > MaximumPackage
            || !IsHash(manifest.Sha256) || !IsHash(manifest.ExecutableSha256))
            throw new InvalidDataException("The update manifest does not match this release.");
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    internal static async Task ExtractVerifiedAsync(string package, string executable, UpdateManifest manifest, CancellationToken token)
    {
        await VerifyHashAsync(package, manifest.Sha256, token);
        using var archive = ZipFile.OpenRead(package);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Stransfer/", "Stransfer/Stransfer.exe", "Stransfer/START-HERE.txt" };
        if (archive.Entries.Count > allowed.Count || archive.Entries.Any(entry => !allowed.Remove(entry.FullName)
            || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000))
            throw new InvalidDataException("The update package contains unexpected files.");
        var app = archive.GetEntry("Stransfer/Stransfer.exe") ?? throw new InvalidDataException("The update package has no executable.");
        if (app.Length != manifest.ExecutableSize) throw new InvalidDataException("The update executable size is invalid.");
        await using (var input = app.Open())
        await using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        { await input.CopyToAsync(output, token); await output.FlushAsync(token); }
        await VerifyHashAsync(executable, manifest.ExecutableSha256, token);
    }

    internal static async Task VerifyHashAsync(string path, string expected, CancellationToken token = default)
    {
        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update failed its SHA-256 integrity check. The current app was kept.");
    }

    internal static void ValidateExecutable(string path, Version expected, string architecture)
    {
        var actual = FileVersionInfo.GetVersionInfo(path);
        if (Normalize(new Version(actual.FileMajorPart, actual.FileMinorPart, actual.FileBuildPart, actual.FilePrivatePart)) != Normalize(expected))
            throw new InvalidDataException("The downloaded executable version is incorrect.");
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("The update is not a Windows executable.");
        stream.Position = 0x3C; var pe = reader.ReadInt32();
        if (pe < 64 || pe > stream.Length - 6) throw new InvalidDataException("The update executable header is invalid.");
        stream.Position = pe;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != (architecture == "win-arm64" ? 0xAA64 : 0x8664))
            throw new InvalidDataException("The update architecture is incorrect.");
    }

    private static void CheckSpace(string path, long required)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))!;
        if (new DriveInfo(root).AvailableFreeSpace < required) throw new IOException("There is not enough free space to stage this update.");
    }

    private async Task<HttpResponseMessage> GetAssetAsync(UpdateAsset asset, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/releases/assets/" + asset.Id);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int max, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > max) throw new InvalidDataException("The release metadata is too large."); await output.WriteAsync(buffer.AsMemory(0, read), token); }
        return output.ToArray();
    }
    public void Dispose() => client.Dispose();
}
