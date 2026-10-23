using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Stransfer.Core;

internal static class VolumeIdentity
{
    public static string Root(string path) => Path.GetPathRoot(Path.GetFullPath(path))!;

    public static string? Get(string root)
    {
        if (!Directory.Exists(root)) return null;
        if (!OperatingSystem.IsWindows()) return "root:" + root;
        var name = new StringBuilder(1024);
        if (GetVolumeNameForVolumeMountPoint(root, name, name.Capacity)) return "volume:" + name.ToString().ToUpperInvariant();
        if (GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0))
            return "serial:" + serial.ToString("X8");
        // Network shares have a stable UNC root, but no local volume GUID.
        return root.StartsWith(@"\\", StringComparison.Ordinal) ? "unc:" + root.ToUpperInvariant() : null;
    }

    public static Dictionary<string, string>? Resolve(IEnumerable<VolumeBinding> bindings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string>? roots = null;
        foreach (var binding in bindings)
        {
            if (string.Equals(Get(binding.Root), binding.Identity, StringComparison.OrdinalIgnoreCase))
            {
                result[binding.Root] = binding.Root;
                continue;
            }
            roots ??= DriveInfo.GetDrives().Select(d => d.Name).ToList();
            var found = roots.FirstOrDefault(root => string.Equals(Get(root), binding.Identity, StringComparison.OrdinalIgnoreCase));
            if (found is null) return null;
            result[binding.Root] = found;
        }
        return result;
    }

    public static string? FileIdentity(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information) || (information.FileIndexHigh == 0 && information.FileIndexLow == 0)) return null;
        return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
    }

    /// <summary>Copy/delete moves cannot remove streams that the buffered copy does not preserve.</summary>
    public static void RejectNamedStreamsForMove(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var fullPath = Path.GetFullPath(path);
        // The extended prefix makes the native enumeration work for long paths
        // independently of the caller's executable manifest/registry setting.
        var nativePath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
            : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
        var search = FindFirstStream(nativePath, 0, out var information, 0);
        if (search == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            // Microsoft documents EOF for no streams and INVALID_PARAMETER for
            // filesystems (such as FAT) that do not support named streams.
            if (error is 38 or 87) return;
            throw new IOException("Source streams could not be checked safely; the source was retained.", new Win32Exception(error));
        }
        try
        {
            while (true)
            {
                if (!string.Equals(information.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"This transfer cannot preserve alternate data streams ({information.StreamName}). The source was retained: {path}");
                if (FindNextStream(search, out information)) continue;
                var error = Marshal.GetLastWin32Error();
                if (error == 38) break;
                throw new IOException("Source streams could not be checked safely; the source was retained.", new Win32Exception(error));
            }
        }
        finally { FindClose(search); }
    }

    public static FileStream OpenForVerifiedDelete(string path, int bufferSize)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // DELETE access lets us delete the verified file by handle. Sharing only READ
        // prevents a rename/replacement/write between verification and deletion.
        var handle = CreateFile(path, 0x80000000u | 0x00010000u, 0x00000001u, IntPtr.Zero,
            3, 0x40000000u | 0x08000000u, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("The source could not be opened safely for a verified move.", new Win32Exception(error));
        }
        try { return new FileStream(handle, FileAccess.Read, bufferSize, isAsync: true); }
        catch { handle.Dispose(); throw; }
    }

    public static void DeleteVerifiedSource(FileStream source, string path)
    {
        if (!OperatingSystem.IsWindows()) { File.Delete(path); return; }
        var info = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(source.SafeFileHandle, 4, ref info, (uint)Marshal.SizeOf<FileDispositionInformation>()))
            throw new IOException("The verified source could not be deleted; the destination is safe and the source was retained.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public static void RenameSource(FileStream source, string destination)
    {
        var name = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        // FILE_RENAME_INFO contains an aligned HANDLE followed by a DWORD byte
        // length and the UTF-16 name. No replacement of an existing file is allowed.
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var size = nameOffset + name.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandleBuffer(source.SafeFileHandle, 3, buffer, (uint)size))
                throw new IOException("The source could not be atomically renamed; it was retained.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindStreamInformation
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber,
        out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemName, int fileSystemNameSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDispositionInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandleBuffer(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstStreamW")]
    private static extern IntPtr FindFirstStream(string path, int informationLevel, out FindStreamInformation information, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextStreamW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr search, out FindStreamInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr search);
}
