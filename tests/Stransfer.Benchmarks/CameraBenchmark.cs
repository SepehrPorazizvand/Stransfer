using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Stransfer.Core;

[SupportedOSPlatform("windows")]
internal static class CameraBenchmark
{
    private sealed record SourceFile(string Relative, long Length, long Modified);
    private sealed record Sample(int Round, string Engine, double Seconds, double MiBPerSecond);

    public static async Task RunAsync(string source, string destinationBase, string reportPath)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); destinationBase = Path.GetFullPath(destinationBase);
        if (!Directory.Exists(source) || !Directory.Exists(destinationBase)) throw new DirectoryNotFoundException("Source and destination drive must exist.");
        var files = new List<SourceFile>(); var directories = new Stack<string>(); directories.Push(source);
        while (directories.TryPop(out var folder))
        {
            RejectLink(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                RejectLink(path);
                if (Directory.Exists(path)) directories.Push(path);
                else { var info = new FileInfo(path); files.Add(new(Path.GetRelativePath(source, path), info.Length, info.LastWriteTimeUtc.Ticks)); }
            }
        }
        long bytes = files.Sum(file => file.Length);
        var drive = new DriveInfo(Path.GetPathRoot(destinationBase)!);
        if (drive.AvailableFreeSpace < bytes * 4 + 1024L * 1024 * 1024) throw new IOException("Insufficient space for four isolated comparison outputs.");
        var name = "Stransfer-Camera-benchmark-" + Guid.NewGuid().ToString("N");
        var outputRoot = Path.GetFullPath(Path.Combine(destinationBase, name));
        var stateBase = Path.GetFullPath(Path.GetTempPath()); var stateRoot = Path.GetFullPath(Path.Combine(stateBase, name));
        if (Directory.Exists(outputRoot) || Directory.Exists(stateRoot) || outputRoot.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Benchmark paths are not isolated.");
        Directory.CreateDirectory(outputRoot); Directory.CreateDirectory(stateRoot);
        var samples = new List<Sample>(); var lastOutputs = new Dictionary<string, string>();
        bool verified = false;
        try
        {
            Console.WriteLine($"Source: {files.Count} files, {bytes:N0} bytes. Two sequential rounds; reverse order in round 2. Originals are read only.");
            for (int round = 0; round < 2; round++)
            {
                foreach (var engine in round == 0 ? new[] { "Windows Shell", "Stransfer Fast" } : new[] { "Stransfer Fast", "Windows Shell" })
                {
                    string output = Path.Combine(outputRoot, $"round-{round + 1}-" + (engine == "Windows Shell" ? "windows" : "stransfer"));
                    Directory.CreateDirectory(output); AssertSource();
                    Console.WriteLine($"START round {round + 1}: {engine}");
                    var clock = Stopwatch.StartNew();
                    if (engine == "Windows Shell") await NativeCopyAsync(source, output);
                    else
                    {
                        using var manager = new TransferManager(Path.Combine(stateRoot, "round-" + round));
                        var job = await manager.CreateJobAsync([source], output, verify: false);
                        await manager.RunAsync(job.Id);
                        if (manager.Jobs.Single().Status != TransferStatus.Completed) throw new IOException(manager.Jobs.Single().Error);
                    }
                    clock.Stop();
                    var copied = Path.Combine(output, Path.GetFileName(source));
                    foreach (var file in files)
                        if (new FileInfo(Path.Combine(copied, file.Relative)).Length != file.Length) throw new IOException("A copied file has a different length.");
                    if (Directory.EnumerateFiles(copied, "*", SearchOption.AllDirectories).Count() != files.Count) throw new IOException("Copied file count differs.");
                    var sample = new Sample(round + 1, engine, clock.Elapsed.TotalSeconds, bytes / 1048576d / clock.Elapsed.TotalSeconds);
                    samples.Add(sample); lastOutputs[engine] = copied;
                    Console.WriteLine($"DONE  round {sample.Round}: {engine}: {sample.Seconds:0.000} s, {sample.MiBPerSecond:0.0} MiB/s");
                }
            }
            Console.WriteLine("Verifying SHA-256 of every source file against the final Windows and Stransfer copies (outside timing)…");
            var buffer = new byte[4 * 1024 * 1024]; int checkedFiles = 0;
            foreach (var file in files)
            {
                var original = await HashAsync(Path.Combine(source, file.Relative), buffer);
                foreach (var output in lastOutputs.Values)
                {
                    var copiedHash = await HashAsync(Path.Combine(output, file.Relative), buffer);
                    if (!original.SequenceEqual(copiedHash)) throw new IOException("Copied content differs: " + file.Relative);
                }
                if (++checkedFiles % 100 == 0) Console.WriteLine($"Verified {checkedFiles}/{files.Count} files.");
            }
            AssertSource(); verified = true;
            var report = new
            {
                timestamp = DateTimeOffset.UtcNow, source = "Desktop/" + Path.GetFileName(source), sourceDrive = Path.GetPathRoot(source),
                destinationDrive = Path.GetPathRoot(destinationBase), files = files.Count, bytes, rounds = 2, verified,
                methodology = "Actual folder; Windows IFileOperation Shell copy engine versus Stransfer Fast. Sequential runs, order reversed in round 2. OS cache not cleared; background activity uncontrolled. Windows Shell can finish with cached writes; Stransfer flushes data before durable publication. SHA-256 verification of both final outputs is outside timing. No source files changed; only unique benchmark directories cleaned.",
                samples,
                averageSeconds = samples.GroupBy(sample => sample.Engine).ToDictionary(group => group.Key, group => group.Average(sample => sample.Seconds))
            };
            await File.WriteAllTextAsync(Path.GetFullPath(reportPath), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS all copied contents verified; source sizes/timestamps unchanged.");
        }
        finally
        {
            // Both targets were created by this invocation. Never recurse into the original source.
            Cleanup(outputRoot, destinationBase, name); Cleanup(stateRoot, stateBase, name);
            Console.WriteLine(verified ? "Removed only the four benchmark outputs and test journals." : "Removed only this invocation's partial benchmark outputs and test journals.");
        }
        void AssertSource()
        {
            foreach (var file in files)
            {
                var info = new FileInfo(Path.Combine(source, file.Relative));
                if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.Modified) throw new IOException("The source changed during comparison.");
            }
        }
    }
    private static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Benchmark sources cannot contain links or junctions."); }
    private static void Cleanup(string path, string parent, string name)
    {
        var resolved = Path.GetFullPath(path);
        if (!resolved.Equals(Path.GetFullPath(Path.Combine(parent, name)), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).Equals(name, StringComparison.Ordinal) || !name.StartsWith("Stransfer-Camera-benchmark-", StringComparison.Ordinal))
            throw new IOException("Unsafe benchmark cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
    private static async Task<byte[]> HashAsync(string path, byte[] buffer)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read; while ((read = await input.ReadAsync(buffer)) != 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }
    private static Task NativeCopyAsync(string source, string destination)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IShellItem? from = null, to = null; IFileOperation? operation = null; bool initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero)); initialized = true;
                var id = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source, IntPtr.Zero, ref id, out from));
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination, IntPtr.Zero, ref id, out to));
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"))!)!;
                operation.SetOperationFlags(0x0004u | 0x0010u | 0x0200u | 0x0400u);
                operation.CopyItem(from, to, null, IntPtr.Zero); operation.PerformOperations(); operation.GetAnyOperationsAborted(out var aborted);
                if (aborted) throw new IOException("The Windows Shell copy was aborted.");
                result.SetResult();
            }
            catch (Exception error) { result.SetException(error); }
            finally { Release(operation); Release(to); Release(from); if (initialized) OleUninitialize(); }
        }) { IsBackground = true, Name = "Windows Shell benchmark" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid id, out IShellItem item);
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags);
        void SetProgressMessage(IntPtr message); void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item); void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, IntPtr name, IntPtr sink); void RenameItems(IntPtr items, IntPtr name);
        void MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink); void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination); void DeleteItem(IntPtr item, IntPtr sink); void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
