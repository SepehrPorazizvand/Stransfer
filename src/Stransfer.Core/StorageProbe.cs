using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Stransfer.Core;

internal sealed record StorageSpace(long AvailableBytes, long ClusterBytes, string FileSystem);

internal static class StorageProbe
{
    public static StorageSpace Read(string existingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(existingDirectory)!);
            return new(drive.AvailableFreeSpace, 4096, drive.DriveFormat);
        }
        if (!GetDiskFreeSpaceEx(existingDirectory, out var available, out _, out _))
            throw new IOException("Destination free space could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        var root = VolumeIdentity.Root(existingDirectory);
        long cluster = GetDiskFreeSpace(root, out var sectors, out var bytes, out _, out _) ? (long)sectors * bytes : 65536;
        var format = new System.Text.StringBuilder(64);
        GetVolumeInformation(root, null, 0, out _, out _, out _, format, format.Capacity);
        return new((long)Math.Min(available, long.MaxValue), Math.Max(cluster, 512), format.ToString());
    }

    public static bool IsNonSeeking(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var root = VolumeIdentity.Root(path);
        if (root.Length != 3 || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
        using var handle = CreateFile(@"\\.\" + root[..2], 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        // STORAGE_PROPERTY_QUERY / StorageDeviceSeekPenaltyProperty (7).
        var query = new byte[12]; BitConverter.GetBytes(7).CopyTo(query, 0);
        var descriptor = new byte[12];
        return DeviceIoControl(handle, 0x002D1400, query, query.Length, descriptor, descriptor.Length, out var returned, IntPtr.Zero)
            && returned >= 9 && descriptor[8] == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpace(string path, out uint sectors, out uint bytes, out uint freeClusters, out uint clusters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformation(string path, System.Text.StringBuilder? label, int labelSize, out uint serial, out uint maxComponent, out uint flags, System.Text.StringBuilder format, int formatSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
