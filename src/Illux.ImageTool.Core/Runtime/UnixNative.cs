using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Illux.ImageTool.Runtime;

/// <summary>
/// The few libc calls .NET has no API for, as used by the root daemon on macOS: the owner and type of a file without
/// following links (lstat), the effective user id, and a lock probe that never blocks. Apple silicon only (the Darwin
/// arm64 layout of <c>struct stat</c>: st_mode (uint16) at offset 4, st_nlink (uint16) at offset 6, st_uid (uint32) at
/// offset 16; 144 bytes in all).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class UnixNative
{
    public const int FifoType = 0x1000;
    public const int DirectoryType = 0x4000;
    public const int RegularFileType = 0x8000;
    public const int SymbolicLinkType = 0xA000;
    public const int SocketType = 0xC000;

    private const int TypeMask = 0xF000;
    private const int PermissionMask = 0xFFF;
    private const int StatBufferSize = 256;

    private const int ENOENT = 2;
    private const int EINTR = 4;
    private const int EACCES = 13;
    private const int ENOTDIR = 20;
    private const int EWOULDBLOCK = 35;
    private const int ELOOP = 62;

    private const int O_RDONLY = 0x0000;
    private const int O_WRONLY = 0x0001;
    private const int O_NONBLOCK = 0x0004;
    private const int O_NOFOLLOW = 0x0100;
    private const int O_CLOEXEC = 0x01000000;
    private const int LOCK_SH = 1;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;

    /// <summary>The type (one of the <c>*Type</c> constants), permission bits, owner and number of names (hard links) of a file.</summary>
    public readonly record struct Status(int Type, UnixFileMode Mode, uint Owner, int LinkCount);

    /// <summary>True on macOS on Apple silicon, the only layout of <c>struct stat</c> this class reads.</summary>
    [SupportedOSPlatformGuard("macos")]
    public static bool IsSupported => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static uint EffectiveUserId => geteuid();

    /// <summary>lstat(2): the entry itself (a link is not followed); null when nothing is there.</summary>
    /// <exception cref="IOException">It cannot be looked at.</exception>
    public static Status? LStat(string path)
    {
        RequireSupported();
        var buffer = new byte[StatBufferSize];
        if (lstat(path, buffer) == 0) return Parse(buffer);
        var errno = Marshal.GetLastPInvokeError();
        if (errno is ENOENT or ENOTDIR) return null;
        throw new IOException($"Cannot look at '{path}' (errno {errno}).");
    }

    /// <summary>
    /// True when <paramref name="path"/> is a regular file another open description holds an exclusive lock on: how
    /// .NET implements <see cref="FileShare.None"/> on Unix (flock). Never blocks and never follows a link: it is opened
    /// read-only with O_NONBLOCK | O_NOFOLLOW, and anything that is not a regular file with exactly one name (a FIFO,
    /// socket or device someone planted, or a hard link to someone else's file) is not a lock. Any failure counts as
    /// "not locked".
    /// </summary>
    public static bool IsLockedExclusively(string path)
    {
        RequireSupported();
        var fd = open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0) return false;
        try
        {
            var buffer = new byte[StatBufferSize];
            if (fstat(fd, buffer) != 0 || !IsSingleRegularFile(Parse(buffer))) return false;
            if (flock(fd, LOCK_SH | LOCK_NB) == 0) return false; // released again by close
            return Marshal.GetLastPInvokeError() == EWOULDBLOCK;
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// Deletes <paramref name="path"/> when it is a regular file with one name that nobody holds a lock on; like
    /// <see cref="IsLockedExclusively"/> it never blocks or follows a link, and leaves anything else alone.
    /// </summary>
    public static void DeleteIfUnlocked(string path)
    {
        RequireSupported();
        var fd = open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0) return;
        try
        {
            var buffer = new byte[StatBufferSize];
            if (fstat(fd, buffer) != 0 || !IsSingleRegularFile(Parse(buffer))) return;
            if (flock(fd, LOCK_EX | LOCK_NB) != 0) return; // held by a live run
            unlink(path); // fails quietly for a file this user may not delete
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// Replaces the contents of the existing regular file <paramref name="path"/> in place: opened write-only with
    /// O_NOFOLLOW | O_NONBLOCK (never a link, never blocking on a FIFO), then only when it is a regular file with exactly
    /// one name: truncated, written, fsync'ed. Nothing is created, so it works in a folder the caller may not write to.
    /// </summary>
    /// <exception cref="FileNotFoundException">Nothing is at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">It is a link, not a regular file, has more than one name, or cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not write it.</exception>
    public static void WriteInPlace(string path, ReadOnlySpan<byte> contents)
    {
        RequireSupported();
        var fd = open(path, O_WRONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno switch
            {
                ENOENT => new FileNotFoundException($"'{path}' does not exist.", path),
                ELOOP => new IOException($"'{path}' is a link; it is left alone."),
                EACCES => new UnauthorizedAccessException($"This account may not write '{path}'."),
                _ => new IOException($"Cannot open '{path}' for writing (errno {errno})."),
            };
        }
        try
        {
            var buffer = new byte[StatBufferSize];
            if (fstat(fd, buffer) != 0) throw new IOException($"Cannot look at '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            if (!IsSingleRegularFile(Parse(buffer)))
                throw new IOException($"'{path}' is not a regular file with a single name; it is left alone.");
            if (ftruncate(fd, 0) != 0) throw new IOException($"Cannot truncate '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            WriteAll(fd, contents, path);
            if (fsync(fd) != 0) throw new IOException($"Cannot flush '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            close(fd);
        }
    }

    private static void WriteAll(int fd, ReadOnlySpan<byte> contents, string path)
    {
        while (!contents.IsEmpty)
        {
            var written = write(fd, ref MemoryMarshal.GetReference(contents), (nuint)contents.Length);
            if (written < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                throw new IOException($"Cannot write '{path}' (errno {errno}).");
            }
            contents = contents[(int)written..];
        }
    }

    /// <summary>A lock file a run made: a regular file whose only name is the one in <c>runs/</c>.</summary>
    private static bool IsSingleRegularFile(Status status) => status.Type == RegularFileType && status.LinkCount == 1;

    private static Status Parse(byte[] buffer)
    {
        var mode = BitConverter.ToUInt16(buffer, 4);
        return new Status(mode & TypeMask, (UnixFileMode)(mode & PermissionMask), BitConverter.ToUInt32(buffer, 16), BitConverter.ToUInt16(buffer, 6));
    }

    private static void RequireSupported()
    {
        if (!IsSupported) throw new PlatformNotSupportedException("This needs macOS on Apple silicon.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, [Out] byte[] buffer);

    // open(2) is variadic; without O_CREAT no mode is passed, so the two fixed arguments are all there is.
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fd, long length);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc")]
    private static extern uint geteuid();
}
