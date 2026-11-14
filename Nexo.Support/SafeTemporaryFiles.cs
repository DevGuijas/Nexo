using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexo.Support;

// Ancestors stay open without write/delete sharing while descendants are enumerated/opened.
// The final file is deleted through its verified handle, never by a second path lookup.
internal static class SafeTemporaryFiles
{
    private const uint ReadAttributes = 0x80, Delete = 0x10000, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x200000, BackupSemantics = 0x2000000;
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { "Downloads", "Documents", "Documentos", "Desktop", "Pictures", "$Recycle.Bin", "Recycler", "SoftwareDistribution", "WindowsUpdate", "Cache", "Cache_Data", "Code Cache", "GPUCache", "Chrome", "Edge", "Firefox", "Mozilla", "Browser", "INetCache" };

    internal static void Clean(string root, DateTime cutoff, Action<string, string, long> record, CancellationToken token)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var volume = Path.GetPathRoot(root)!;
        if (root.Length <= volume.Length || volume.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Raiz temporária inválida.");
        var held = new List<SafeFileHandle>();
        try
        {
            var current = volume;
            foreach (var part in root[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                if (Excluded.Contains(part)) throw new IOException("Raiz temporária dentro de pasta protegida.");
                current = Path.Combine(current, part);
                held.Add(OpenDirectory(current));
            }
            Walk(root, cutoff, record, token, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { record(root, "Unavailable", 0); }
        finally { foreach (var handle in held) handle.Dispose(); }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFile(path, ReadAttributes, 1, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Diretório indisponível."); }
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || (info.Attributes & (uint)FileAttributes.Directory) == 0)
        { handle.Dispose(); throw new IOException("Diretório não seguro."); }
        return handle;
    }

    private static void Walk(string directory, DateTime cutoff, Action<string, string, long> record, CancellationToken token, int depth)
    {
        if (depth > 64) { record(directory, "DepthLimit", 0); return; }
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (Excluded.Contains(Path.GetFileName(path))) { record(path, "Protected", 0); continue; }
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { record(path, "ReparsePoint", 0); continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        using var pinned = OpenDirectory(path);
                        Walk(path, cutoff, record, token, depth + 1);
                        continue;
                    }
                    using var file = CreateFile(path, Delete | ReadAttributes, 1, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
                    if (file.IsInvalid) { record(path, NativeFailureOutcome(), 0); continue; }
                    if (!GetFileInformationByHandle(file, out var info)) { record(path, "Unavailable", 0); continue; }
                    if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.System)) != 0
                        || info.NumberOfLinks != 1) { record(path, "Protected", 0); continue; }
                    if (FileTime(info.LastWriteTime) >= cutoff || FileTime(info.CreationTime) >= cutoff) { record(path, "Recent", 0); continue; }
                    token.ThrowIfCancellationRequested();
                    // A durable intent must be accepted before an irreversible deletion.
                    record(path, "Deleting", 0);
                    var disposition = new Disposition { DeleteFile = true };
                    if (!SetFileInformationByHandle(file, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>())) { record(path, NativeFailureOutcome(), 0); continue; }
                    var length = ((long)info.SizeHigh << 32) | info.SizeLow;
                    file.Dispose();
                    record(path, "Completed", length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { record(path, "Unavailable", 0); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { record(directory, "Unavailable", 0); }
    }

    // Only explicit Windows sharing/lock violations are expected skips; ACL and other native errors are operational failures.
    private static string NativeFailureOutcome() => Marshal.GetLastPInvokeError() is 32 or 33 ? "Locked" : "Unavailable";

    private static DateTime FileTime(System.Runtime.InteropServices.ComTypes.FILETIME value) => DateTime.FromFileTimeUtc(((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime);
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref Disposition info, uint size);
}
