using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Stransfer.Core;

/// <summary>Pin validated Windows ancestors without delete sharing, so per-file checks can reuse them safely.</summary>
internal sealed class DirectoryGuard : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> pinned = new(StringComparer.OrdinalIgnoreCase);
    public void CheckFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) RejectLink(File.GetAttributes(path), path);
        Pin(Path.GetDirectoryName(path)!);
    }
    public void Pin(string path)
    {
        lock (pinned)
        {
            string? current = Path.GetFullPath(path);
            while (current is not null && !pinned.ContainsKey(current))
            {
                if (!OperatingSystem.IsWindows())
                {
                    RejectLink(File.GetAttributes(current), current);
                    current = Path.GetDirectoryName(current); continue;
                }
                var handle = CreateFile(current, 0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("A transfer folder could not be locked: " + current, new Win32Exception(Marshal.GetLastWin32Error())); }
                try
                {
                    if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Folder attributes could not be read.");
                    RejectLink((FileAttributes)info.Attributes, current);
                    pinned.Add(current, handle);
                }
                catch { handle.Dispose(); throw; }
                current = Path.GetDirectoryName(current);
            }
        }
    }
    private static void RejectLink(FileAttributes attributes, string path)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Links and junctions are not followed: " + path);
    }
    public void Dispose()
    { lock (pinned) { foreach (var handle in pinned.Values) handle.Dispose(); pinned.Clear(); } }
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes; public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
}
