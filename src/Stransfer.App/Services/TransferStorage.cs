using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using Stransfer.Core;

namespace Stransfer.App.Services;

internal sealed record StorageEntry(string Location, string Name, bool Directory, long Length, long Modified);
internal sealed record StorageCapacity(long FreeBytes, long ClusterBytes, string Format);

/// <summary>Filesystem and Shell namespace endpoints. COM objects stay on the engine's STA.</summary>
internal class TransferStorage : IDisposable
{
    internal const string Prefix = "shell:";
    private readonly Dictionary<string, IShellItem> items = new(StringComparer.Ordinal);
    private readonly DirectoryGuard guard = new();
    private readonly Dictionary<string, Dictionary<string, StorageEntry>> nameIndexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Parent, string Name)> parents = new(StringComparer.Ordinal);
    internal static bool IsShell(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);
    internal static string Encode(string parsingName)
    {
        var item = NamespaceCom.Open(parsingName);
        try { return Encode(item); } finally { NamespaceCom.Release(item); }
    }
    internal static string Encode(IShellItem item)
    {
        var name = NamespaceCom.Name(item, 0x80028000);
        Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item, out var pointer));
        try
        {
            var length = checked((int)ILGetSize(pointer)); var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
            return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ShellLocation(name, bytes)));
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    internal static string Decode(string location) => Parse(location).Name;
    private static ShellLocation Parse(string location) => JsonSerializer.Deserialize<ShellLocation>(Convert.FromBase64String(location[Prefix.Length..])) ?? throw new InvalidDataException("Invalid device location.");
    private static IShellItem OpenLocation(string location)
    {
        if (!IsShell(location)) return NamespaceCom.Open(location);
        var value = Parse(location); var bytes = value.IdList;
        int offset = 0;
        while (offset + 2 <= bytes.Length)
        {
            var size = BitConverter.ToUInt16(bytes, offset);
            if (size == 0) break;
            if (size < 2 || offset + size > bytes.Length) throw new InvalidDataException("Invalid device item identifier.");
            offset += size;
        }
        if (bytes.Length > 65536 || offset + 2 != bytes.Length || BitConverter.ToUInt16(bytes, offset) != 0) throw new InvalidDataException("Invalid device item identifier.");
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length); var id = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(pointer, ref id, out var item)); return item;
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    internal static string Display(string location) => IsShell(location) ? Decode(location) : location;
    internal IShellItem Item(string location)
    {
        if (!items.TryGetValue(location, out var item)) items[location] = item = OpenLocation(location);
        return item;
    }
    internal string Remember(IShellItem item)
    {
        var location = Encode(item);
        if (!items.TryAdd(location, item)) NamespaceCom.Release(item);
        return location;
    }
    public virtual StorageEntry Stat(string location)
    {
        if (!IsShell(location))
        {
            CheckLocal(location);
            var attributes = File.GetAttributes(location);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.Encrypted) != 0) throw new NotSupportedException("Decrypt this file before transferring it to a device.");
            return new(location, Path.GetFileName(Path.TrimEndingDirectorySeparator(location)), directory,
                directory ? 0 : new FileInfo(location).Length, File.GetLastWriteTimeUtc(location).Ticks);
        }
        var item = Item(location);
        Marshal.ThrowExceptionForHR(item.GetAttributes(0x20000000u | 0x10000u, out var flags));
        if ((flags & 0x10000) != 0) throw new NotSupportedException("Links are not followed during a device transfer.");
        bool folder = (flags & 0x20000000) != 0;
        long length = 0, modified = 0;
        if (!folder)
        {
            if (TryNumber(item, new("b725f130-47ef-101a-a5f1-02608c9eebac"), 12, out var size))
            {
                length = checked((long)size);
                if (TryNumber(item, new("b725f130-47ef-101a-a5f1-02608c9eebac"), 14, out var date)) modified = checked((long)date);
            }
            else
            {
                using var input = Read(location); length = input.Length;
            }
        }
        var name = FileName(item);
        return new(location, name, folder, length, modified);
    }
    public virtual IReadOnlyList<StorageEntry> Children(string folder)
    {
        if (!IsShell(folder))
        {
            CheckLocal(folder);
            return Directory.EnumerateFileSystemEntries(folder).Select(Stat).ToArray();
        }
        var entries = new List<StorageEntry>();
        var enumerator = NamespaceCom.Bind<IEnumShellItems>(Item(folder), NamespaceCom.Enumeration);
        try
        {
            while (true)
            {
                var hr = enumerator.Next(1, out var item, out var count); Marshal.ThrowExceptionForHR(hr);
                if (count == 0) break;
                entries.Add(Stat(Remember(item)));
            }
        }
        finally { NamespaceCom.Release(enumerator); }
        return entries;
    }
    public virtual IReadOnlyList<StorageEntry> Names(string folder)
    {
        if (!TransferStorage.IsShell(folder)) return Directory.EnumerateFileSystemEntries(folder)
            .Select(path => new StorageEntry(path, Path.GetFileName(path), Directory.Exists(path), -1, 0)).ToArray();
        if (!nameIndexes.TryGetValue(folder, out var index))
        {
            index = new(StringComparer.OrdinalIgnoreCase);
            var enumerator = NamespaceCom.Bind<IEnumShellItems>(Item(folder), NamespaceCom.Enumeration);
            try
            {
                while (true)
                {
                    Marshal.ThrowExceptionForHR(enumerator.Next(1, out var item, out var count)); if (count == 0) break;
                    var location = Remember(item); var cached = Item(location);
                    Marshal.ThrowExceptionForHR(cached.GetAttributes(0x20000000, out var attributes));
                    var entry = new StorageEntry(location, FileName(cached), (attributes & 0x20000000) != 0, -1, 0);
                    if (!index.TryAdd(entry.Name, entry)) throw new IOException("Duplicate device filenames cannot be transferred safely.");
                    parents[location] = (folder, entry.Name);
                }
            }
            finally { NamespaceCom.Release(enumerator); }
            nameIndexes[folder] = index;
        }
        return index.Values.ToArray();
    }
    public virtual StorageEntry? Find(string folder, string name)
    {
        if (!IsShell(folder)) { var path = Path.Combine(folder, name); return File.Exists(path) || Directory.Exists(path) ? Stat(path) : null; }
        if (!nameIndexes.ContainsKey(folder)) Names(folder);
        return nameIndexes[folder].TryGetValue(name, out var entry) ? Stat(entry.Location) : null;
    }
    public virtual string RefreshWritten(string parent, string name, string location)
    {
        // WPD replaces provisional IDs on Commit. Enumerate names only, without reading every file's metadata.
        nameIndexes.Remove(parent);
        return Find(parent, name)?.Location ?? throw new IOException("The device did not publish its written file.");
    }
    public void ReleaseFolderLocks() => guard.Dispose();
    public virtual Stream Read(string location)
    {
        if (!IsShell(location)) { guard.CheckFile(location); return new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan); }
        IStream stream;
        try { stream = NamespaceCom.Bind<IStream>(Item(location), NamespaceCom.Stream); }
        catch (COMException)
        {
            var resources = Resources(Item(location));
            try
            {
                var resource = Content(resources); var id = typeof(IStream).GUID;
                resources.OpenResource(ref resource, ref id, out var pointer);
                try { stream = (IStream)Marshal.GetObjectForIUnknown(pointer); } finally { Marshal.Release(pointer); }
            }
            finally { NamespaceCom.Release(resources); }
        }
        return new NamespaceStream(stream, writable: false);
    }
    public virtual (string Location, Stream Stream) Create(string parent, string name, long size)
    {
        SafeName(name);
        if (!IsShell(parent))
        {
            var path = Path.Combine(parent, name); guard.CheckFile(path);
            return (path, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.SequentialScan));
        }
        var target = NamespaceCom.Bind<ITransferDestination>(Item(parent), NamespaceCom.Transfer);
        IShellItemResources? resources = null; IShellItem? item = null;
        try
        {
            var itemId = typeof(IShellItem).GUID; var resourcesId = typeof(IShellItemResources).GUID;
            target.CreateItem(name, 0x80, (ulong)size, 0, ref itemId, out item, ref resourcesId, out resources);
            var location = Remember(item); item = null;
            if (nameIndexes.TryGetValue(parent, out var index)) index[name] = new(location, name, false, size, 0);
            parents[location] = (parent, name);
            NamespaceCom.Release(resources); resources = null;
            return (location, new NamespaceStream(NamespaceCom.WritableStream(Item(location)), writable: true));
        }
        finally { NamespaceCom.Release(item); NamespaceCom.Release(resources); NamespaceCom.Release(target); }
    }
    public virtual Stream ReopenWrite(string location)
    {
        if (IsShell(location)) throw new NotSupportedException("This device restarts the current incomplete file; completed files are retained.");
        guard.CheckFile(location);
        return new FileStream(location, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.SequentialScan);
    }
    public virtual string CreateFolder(string parent, string name)
    {
        SafeName(name);
        if (!IsShell(parent))
        {
            var path = Path.Combine(parent, name); CheckLocal(parent);
            Directory.CreateDirectory(path); guard.Pin(path); return path;
        }
        var target = NamespaceCom.Bind<ITransferDestination>(Item(parent), NamespaceCom.Transfer);
        IShellItem? item = null; IShellItemResources? resources = null;
        try
        {
            var itemId = typeof(IShellItem).GUID; var resourceId = typeof(IShellItemResources).GUID;
            target.CreateItem(name, 0x10, 0, 0, ref itemId, out item, ref resourceId, out resources);
            var location = Remember(item); item = null;
            if (nameIndexes.TryGetValue(parent, out var index)) index[name] = new(location, name, true, 0, 0);
            parents[location] = (parent, name); return location;
        }
        finally { NamespaceCom.Release(resources); NamespaceCom.Release(item); NamespaceCom.Release(target); }
    }
    public virtual string Rename(string location, string parent, string name)
    {
        SafeName(name);
        if (!IsShell(location))
        {
            var target = Path.Combine(parent, name); guard.CheckFile(location); guard.CheckFile(target);
            File.Move(location, target, overwrite: false); return target;
        }
        var transfer = NamespaceCom.Bind<ITransferSource>(Item(parent), NamespaceCom.Transfer);
        try
        {
            try { transfer.RenameItem(Item(location), name, 0, out var item); return Renamed(Remember(item)); }
            catch (COMException error) when (error.HResult is unchecked((int)0x800300FC) or unchecked((int)0x80004001))
            {
                // Some WPD providers implement the folder rename API rather than ITransferSource.RenameItem.
                var folder = NamespaceCom.Bind<INamespaceFolder>(Item(parent), new Guid("3981e224-f559-11d3-8e3a-00c04f6837d5"));
                Marshal.ThrowExceptionForHR(SHGetIDListFromObject(Item(location), out var absolute));
                IntPtr renamed = IntPtr.Zero, parentId = IntPtr.Zero;
                try
                {
                    folder.SetNameOf(IntPtr.Zero, ILFindLastID(absolute), name, 0, out renamed);
                    Marshal.ThrowExceptionForHR(SHGetIDListFromObject(Item(parent), out parentId));
                    var id = typeof(IShellItem).GUID;
                    Marshal.ThrowExceptionForHR(SHCreateItemWithParent(parentId, folder, renamed, ref id, out var item));
                    return Renamed(Remember(item));
                }
                finally { Marshal.FreeCoTaskMem(absolute); Marshal.FreeCoTaskMem(renamed); Marshal.FreeCoTaskMem(parentId); NamespaceCom.Release(folder); }
            }
        }
        finally { NamespaceCom.Release(transfer); }
        string Renamed(string result)
        {
            if (nameIndexes.TryGetValue(parent, out var index))
            {
                if (parents.TryGetValue(location, out var previous)) index.Remove(previous.Name);
                index[name] = new(result, name, false, -1, 0);
            }
            parents[result] = (parent, name); return result;
        }
    }
    public virtual void Delete(string location)
    {
        if (!IsShell(location))
        {
            CheckLocal(location);
            if (Directory.Exists(location)) Directory.Delete(location, recursive: false); else File.Delete(location);
            return;
        }
        var item = Item(location); Marshal.ThrowExceptionForHR(item.GetParent(out var parent));
        try
        {
            var transfer = NamespaceCom.Bind<ITransferSource>(parent, NamespaceCom.Transfer);
            try { transfer.RemoveItem(item, 0); } finally { NamespaceCom.Release(transfer); }
        }
        finally { NamespaceCom.Release(parent); }
        if (items.Remove(location, out var cached)) NamespaceCom.Release(cached);
        if (parents.Remove(location, out var previous) && nameIndexes.TryGetValue(previous.Parent, out var index)) index.Remove(previous.Name);
    }
    public virtual StorageCapacity Capacity(string location)
    {
        if (!IsShell(location)) { var space = StorageProbe.Read(location); return new(space.AvailableBytes, space.ClusterBytes, space.FileSystem); }
        // FreeSpace belongs to the storage object, which may be an ancestor of the selected folder.
        IShellItem? current = OpenLocation(location);
        try
        {
            for (int level = 0; level < 16 && current is not null; level++)
            {
                IPropertyStore? store = null;
                try
                {
                    store = Properties(current);
                    var key = new PropertyKey { Format = new("9b174b35-40ff-11d2-a27e-00c04fc30871"), Id = 2 };
                    store.GetValue(ref key, out var value);
                    try { if (value.Type == 21) return new(checked((long)value.Unsigned), 65536, "Portable device"); }
                    finally { PropVariantClear(ref value); }
                }
                catch (COMException) { }
                finally { NamespaceCom.Release(store); }
                if (current.GetParent(out var parent) < 0) break;
                NamespaceCom.Release(current); current = parent;
            }
        }
        finally { NamespaceCom.Release(current); }
        throw new IOException("The device did not report free space. Unlock it and enable File transfer (MTP), then retry.");
    }
    public virtual void CheckWritable(string folder)
    {
        if (IsShell(folder)) { var target = NamespaceCom.Bind<ITransferDestination>(Item(folder), NamespaceCom.Transfer); NamespaceCom.Release(target); return; }
        CheckLocal(folder);
        var path = Path.Combine(folder, ".stransfer-check-" + Guid.NewGuid().ToString("N"));
        using var probe = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
    public void ApplyModifiedTime(string destination, long modified, bool fromShell)
    {
        if (modified <= 0 || IsShell(destination)) return;
        CheckLocal(destination);
        File.SetLastWriteTimeUtc(destination, fromShell ? DateTime.FromFileTimeUtc(modified) : new DateTime(modified, DateTimeKind.Utc));
    }
    internal static void SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || IsDeviceName(name)) throw new IOException("The destination cannot represent this file name: " + name);
    }
    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }
    private static void CheckLocal(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links and junctions are not followed: " + current);
    }
    private static IShellItemResources Resources(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(item.GetParent(out var parent));
        try
        {
            var transfer = NamespaceCom.Bind<ITransferSource>(parent, NamespaceCom.Transfer);
            try
            {
                var id = typeof(IShellItemResources).GUID; transfer.OpenItem(item, 0, ref id, out var pointer);
                if (pointer == IntPtr.Zero) throw new NotSupportedException("The device did not expose item resources.");
                try { return (IShellItemResources)Marshal.GetObjectForIUnknown(pointer); } finally { Marshal.Release(pointer); }
            }
            finally { NamespaceCom.Release(transfer); }
        }
        finally { NamespaceCom.Release(parent); }
    }
    private static ShellResource Content(IShellItemResources resources)
    {
        resources.EnumResources(out var enumerator);
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.Next(1, out var resource, out var count));
            return count == 1 ? resource : new ShellResource { Type = Guid.Empty, Name = "" };
        }
        finally { NamespaceCom.Release(enumerator); }
    }
    private static bool TryNumber(IShellItem item, Guid format, uint id, out ulong number)
    {
        number = 0; IPropertyStore? store = null;
        try
        {
            store = Properties(item);
            var key = new PropertyKey { Format = format, Id = id }; store.GetValue(ref key, out var value);
            try
            {
                if (value.Type is 21 or 20 or 64) { number = value.Unsigned; return true; }
                if (value.Type == 19) { number = value.Unsigned & uint.MaxValue; return true; }
                return false;
            }
            finally { PropVariantClear(ref value); }
        }
        catch (COMException) { return false; }
        finally { NamespaceCom.Release(store); }
    }
    private static string FileName(IShellItem item)
    {
        IPropertyStore? store = null;
        try
        {
            store = Properties(item);
            var key = new PropertyKey { Format = new("41cf5ae0-f75a-4806-bd87-59c7d9248eb9"), Id = 100 };
            store.GetValue(ref key, out var value);
            try { if (value.Type == 31 && value.Pointer != IntPtr.Zero && Marshal.PtrToStringUni(value.Pointer) is { Length: > 0 } name) return name; }
            finally { PropVariantClear(ref value); }
        }
        catch (COMException) { }
        finally { NamespaceCom.Release(store); }
        // Parsing names on MTP are object IDs, whereas editing names preserve file extensions.
        return NamespaceCom.Name(item, 0x80031001);
    }
    private static IPropertyStore Properties(IShellItem item)
    {
        var id = typeof(IPropertyStore).GUID;
        ((IShellItemProperties)item).GetPropertyStore(0, ref id, out var pointer);
        if (pointer == IntPtr.Zero) throw new IOException("The device did not return file properties.");
        try { return (IPropertyStore)Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }
    public void Dispose() { guard.Dispose(); foreach (var item in items.Values) NamespaceCom.Release(item); items.Clear(); }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public ulong Unsigned; [FieldOffset(8)] public IntPtr Pointer; }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out Variant value);
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
    [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject(IShellItem item, out IntPtr idList);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr idList);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(IntPtr idList, ref Guid id, out IShellItem item);
    [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr idList);
    [DllImport("shell32.dll")] private static extern int SHCreateItemWithParent(IntPtr parentId, INamespaceFolder parent, IntPtr childId, ref Guid id, out IShellItem item);
    private sealed record ShellLocation(string Name, byte[] IdList);
}

internal sealed class NamespaceStream : Stream
{
    private readonly IStream stream;
    private readonly bool writable;
    private readonly IntPtr count = Marshal.AllocCoTaskMem(8);
    private long position;
    private bool disposed;
    public NamespaceStream(IStream stream, bool writable) { this.stream = stream; this.writable = writable; }
    public override bool CanRead => !disposed && !writable;
    public override bool CanWrite => !disposed && writable;
    public override bool CanSeek => false;
    public override long Length { get { stream.Stat(out var stat, 1); return stat.cbSize; } }
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offset != 0) throw new NotSupportedException("Namespace reads use pooled buffers from offset zero.");
        stream.Read(buffer, length, count); int read = Marshal.ReadInt32(count);
        if (read < 0 || read > length) throw new IOException("Invalid device read count.");
        position += read; return read;
    }
    public override void Write(byte[] buffer, int offset, int length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offset != 0) throw new NotSupportedException();
        stream.Write(buffer, length, count);
        if (Marshal.ReadInt32(count) != length) throw new IOException("The device could not write the complete block.");
        position += length;
    }
    public override void Flush() { if (writable) stream.Commit(0); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (!disposed) { disposed = true; Marshal.FreeCoTaskMem(count); NamespaceCom.Release(stream); }
        base.Dispose(disposing);
    }
}
