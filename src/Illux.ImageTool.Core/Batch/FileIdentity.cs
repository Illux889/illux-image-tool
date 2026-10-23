using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Illux.ImageTool.Batch;

/// <summary>Decides whether two paths name the same file, also through another case, dot segments, junctions or a mapped drive.</summary>
internal static class FileIdentity
{
    public static bool AreSame(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
        if (!OperatingSystem.IsWindows() || !File.Exists(a) || !File.Exists(b)) return false;
        return TryGetId(a) is { } idA && TryGetId(b) is { } idB && idA == idB;
    }

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
