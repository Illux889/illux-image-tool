using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Illux.ImageTool.Runtime;

/// <summary>
/// The few libc calls .NET has no API for, as used on macOS by the root job and the folders job (V15) in folders others
/// may write to: the owner and type of a file without following links (lstat), the effective user id, opens that never
/// follow a link or block on a FIFO, an exclusive rename and lock probes that never block. Apple silicon only (the
/// Darwin arm64 layout of <c>struct stat</c>: st_mode (uint16) at offset 4, st_nlink (uint16) at offset 6, st_uid
/// (uint32) at offset 16; 144 bytes in all).
/// open(2) is variadic, and on Apple silicon a variadic argument is passed differently from a fixed one, so it is only
/// ever called here without O_CREAT (two arguments, no mode); files are created through .NET (FileMode.CreateNew, which
/// is O_CREAT | O_EXCL and therefore never follows a link either).
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
    private const int EEXIST = 17;
    private const int EXDEV = 18;
    private const int ENOTDIR = 20;
    private const int EWOULDBLOCK = 35;
    private const int ELOOP = 62;

    private const int O_RDONLY = 0x0000;
    private const int O_WRONLY = 0x0001;
    private const int O_NONBLOCK = 0x0004;
    private const int O_APPEND = 0x0008;
    private const int O_NOFOLLOW = 0x0100;
    private const int O_CLOEXEC = 0x01000000;
    private const int LOCK_SH = 1;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const uint RENAME_EXCL = 0x00000004;

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
    /// Opens <paramref name="path"/> with <paramref name="flags"/> plus O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC (never a link,
    /// never blocking on a FIFO, never passed to a child) and keeps it open only when fstat says it is a regular file.
    /// Returns the descriptor (close it with <see cref="Close"/>), or -1: then <paramref name="errno"/> is the open error,
    /// or 0 when something else than a regular file is there (<paramref name="status"/> says what).
    /// </summary>
    public static int TryOpenRegular(string path, int flags, out Status status, out int errno)
    {
        RequireSupported();
        status = default;
        var fd = open(path, flags | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return -1;
        }
        var buffer = new byte[StatBufferSize];
        if (fstat(fd, buffer) != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            close(fd);
            return -1;
        }
        errno = 0;
        status = Parse(buffer);
        if (status.Type == RegularFileType) return fd;
        close(fd);
        return -1;
    }

    public static void Close(int fd) => close(fd);

    /// <summary>
    /// True when <paramref name="path"/> is a regular file another open description holds an exclusive lock on: how
    /// .NET implements <see cref="FileShare.None"/> on Unix (flock). Never blocks and never follows a link (see
    /// <see cref="TryOpenRegular"/>); anything that is not a regular file with exactly one name (a FIFO, socket or
    /// device someone planted, or a hard link to someone else's file) is not a lock. Any failure counts as "not locked".
    /// </summary>
    public static bool IsLockedExclusively(string path)
    {
        var fd = TryOpenRegular(path, O_RDONLY, out var status, out _);
        if (fd < 0) return false;
        try
        {
            if (status.LinkCount != 1) return false;
            if (flock(fd, LOCK_SH | LOCK_NB) == 0) return false; // released again by close
            return Marshal.GetLastPInvokeError() == EWOULDBLOCK;
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// For a file in a watched In folder (I1): true when it is a regular file (not a link, FIFO, socket or device) that
    /// nobody holds a lock on, which is what an open with <see cref="FileShare.None"/> asks on Unix; it never blocks.
    /// </summary>
    public static bool CanLockExclusively(string path)
    {
        var fd = TryOpenRegular(path, O_RDONLY, out _, out _);
        if (fd < 0) return false;
        try
        {
            return flock(fd, LOCK_EX | LOCK_NB) == 0; // released again by close
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
        var fd = TryOpenRegular(path, O_RDONLY, out var status, out _);
        if (fd < 0) return;
        try
        {
            if (status.LinkCount != 1) return;
            if (flock(fd, LOCK_EX | LOCK_NB) != 0) return; // held by a live run
            unlink(path); // fails quietly for a file this user may not delete
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// Replaces the contents of the existing regular file <paramref name="path"/> in place: opened write-only (see
    /// <see cref="TryOpenRegular"/>: never a link, never blocking on a FIFO), then written from the start, cut to the new
    /// length, fsync'ed. Nothing is created, so it works in a folder the caller may not write to. A second name (hard
    /// link) does not matter: the caller checks that only root can put entries in the folder (V14), so the file is the
    /// installer's; other names show the same content.
    /// </summary>
    /// <exception cref="FileNotFoundException">Nothing is at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">It is a link, not a regular file, or cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not write it.</exception>
    public static void WriteInPlace(string path, ReadOnlySpan<byte> contents)
    {
        var fd = TryOpenRegular(path, O_WRONLY, out _, out var errno);
        if (fd < 0)
        {
            throw errno switch
            {
                0 => new IOException($"'{path}' is not a regular file; it is left alone."),
                ENOENT => new FileNotFoundException($"'{path}' does not exist.", path),
                ELOOP => new IOException($"'{path}' is a link; it is left alone."),
                EACCES => new UnauthorizedAccessException($"This account may not write '{path}'."),
                _ => new IOException($"Cannot open '{path}' for writing (errno {errno})."),
            };
        }
        try
        {
            // Written from offset 0 over the old contents, then cut to the new length: the file is never empty meanwhile.
            WriteAll(fd, contents, path);
            if (ftruncate(fd, contents.Length) != 0) throw new IOException($"Cannot truncate '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            if (fsync(fd) != 0) throw new IOException($"Cannot flush '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// C1: appends to a log file in a folder others may write to (<c>_warnings.log</c> in Out). It is created when
    /// missing (mode 0644; O_CREAT | O_EXCL, never through a link), then opened with O_APPEND (never a link, never
    /// blocking) and written only when it is a regular file with one name that this account owns. Returns null when
    /// written, otherwise why nothing was written.
    /// </summary>
    public static string? AppendToOwnFile(string path, ReadOnlySpan<byte> contents)
    {
        RequireSupported();
        var fd = TryOpenRegular(path, O_WRONLY | O_APPEND, out var status, out var errno);
        if (fd < 0 && errno == ENOENT)
        {
            try
            {
                using (new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                }))
                {
                }
            }
            catch (IOException)
            {
                // Someone else made it in the meantime: it is checked like any other existing file below.
            }
            fd = TryOpenRegular(path, O_WRONLY | O_APPEND, out status, out errno);
        }
        if (fd < 0)
        {
            return errno switch
            {
                0 => "it is not a regular file",
                ELOOP => "it is a link",
                _ => $"it cannot be opened (errno {errno})",
            };
        }
        try
        {
            if (status.LinkCount != 1) return "it has more than one name (hard link)";
            if (status.Owner != geteuid()) return $"it is owned by user {status.Owner}, not by this account";
            WriteAll(fd, contents, path);
            return null;
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// C2: moves the staged file <paramref name="from"/> to <paramref name="to"/> with renamex_np(RENAME_EXCL): in one
    /// step, and only when nothing (not even a link) has that name. On another volume (EXDEV) it is copied with
    /// <see cref="CopyExclusive"/> instead.
    /// </summary>
    /// <exception cref="IOException">The name is taken (<see cref="IsAlreadyExists"/>), or the move failed.</exception>
    public static void MoveExclusive(string from, string to)
    {
        RequireSupported();
        if (renamex_np(from, to, RENAME_EXCL) == 0) return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == EXDEV)
        {
            CopyExclusive(from, to);
            return;
        }
        throw new IOException(
            errno == EEXIST ? $"'{to}' already exists; it is not replaced." : $"Cannot move '{from}' to '{to}' (errno {errno}).",
            errno == EEXIST ? EEXIST : errno);
    }

    /// <summary>
    /// Copies <paramref name="from"/> into a new file <paramref name="to"/> (O_CREAT | O_EXCL: never an existing file or a
    /// link) and then deletes <paramref name="from"/>. A copy that fails is removed again.
    /// </summary>
    public static void CopyExclusive(string from, string to)
    {
        RequireSupported();
        using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            FileStream target;
            try
            {
                target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (LStat(to) is not null)
            {
                throw new IOException($"'{to}' already exists; it is not replaced.", ex) { HResult = EEXIST };
            }

            var copied = false;
            try
            {
                using (target)
                {
                    source.CopyTo(target);
                    target.Flush(flushToDisk: true);
                }
                copied = true;
            }
            finally
            {
                if (!copied) unlink(to); // the half copy this call created
            }
        }
        File.Delete(from);
    }

    /// <summary>True when <paramref name="ex"/> says the target name was taken (EEXIST, as thrown by this class or by .NET).</summary>
    public static bool IsAlreadyExists(IOException ex) => ex.HResult == EEXIST;

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

    [DllImport("libc", SetLastError = true)]
    private static extern int renamex_np([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

    [DllImport("libc")]
    private static extern uint geteuid();
}
