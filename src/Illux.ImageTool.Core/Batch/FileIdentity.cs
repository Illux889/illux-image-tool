using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Illux.ImageTool.Batch;

/// <summary>
/// Decides whether two paths name the same file, also through another case, dot segments, junctions or a mapped drive
/// (Windows: volume serial and file index), or symbolic links and hard links (macOS: device and inode).
/// </summary>
internal static class FileIdentity
{
    public static bool AreSame(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
        if (OperatingSystem.IsWindows())
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            return TryGetId(a) is { } idA && TryGetId(b) is { } idB && idA == idB;
        }
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            return TryGetMacId(a) is { } idA && TryGetMacId(b) is { } idB && idA == idB;
        }
        return false;
    }

    /// <summary>
    /// st_dev and st_ino from stat(2), which follows links. Layout of struct stat on Darwin arm64 (64-bit inodes, the
    /// only layout there): dev_t st_dev (int32) at offset 0, ino_t st_ino (uint64) at offset 8; 144 bytes in all.
    /// </summary>
    private static (int Device, ulong Inode)? TryGetMacId(string path)
    {
        var buffer = new byte[256];
        if (DarwinStat(path, buffer) != 0) return null;
        return (BitConverter.ToInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8));
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int DarwinStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    private static (uint Volume, uint High, uint Low)? TryGetId(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info)
                ? (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIME is two DWORDs: 4-byte aligned
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
