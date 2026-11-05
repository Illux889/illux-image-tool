using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Illux.ImageTool.Settings;

/// <summary>
/// On macOS config.json is root:admin 0664: only members of the <c>admin</c> group may change the settings. Others
/// see the window read-only. Read from the user's full group list (getpwuid + getgrouplist, which asks the directory
/// service and is not limited to the 16 groups getgroups reports), the effective group (getegid) and the group database
/// (getgrnam), without starting another program.
/// </summary>
internal static class MacAdminGroup
{
    public const string GroupName = "admin";

    private const int MaxGroups = 4096;

    /// <summary>True when <paramref name="adminGroup"/> exists and is the effective group or one of <paramref name="groups"/>.</summary>
    public static bool IsMember(IReadOnlyCollection<uint> groups, uint effectiveGroup, uint? adminGroup) =>
        adminGroup is { } admin && (effectiveGroup == admin || groups.Contains(admin));

    /// <summary>Whether this user may write config.json; false when it cannot be told.</summary>
    [SupportedOSPlatform("macos")]
    public static bool CurrentUserIsMember()
    {
        try
        {
            return IsMember(UserGroups(), getegid(), AdminGroupId());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Every group of the user this process runs as (getgrouplist with the user's primary group).</summary>
    [SupportedOSPlatform("macos")]
    private static uint[] UserGroups()
    {
        // struct passwd { char *pw_name; char *pw_passwd; uid_t pw_uid; gid_t pw_gid; ... }: pw_gid at offset 20 on 64-bit.
        var passwd = getpwuid(geteuid());
        if (passwd == IntPtr.Zero) throw new InvalidOperationException("This user is not in the user database.");
        var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(passwd, 0))
            ?? throw new InvalidOperationException("This user has no name.");
        var primary = Marshal.ReadInt32(passwd, 2 * IntPtr.Size + sizeof(uint));

        for (var size = 64; size <= MaxGroups; size *= 4)
        {
            var groups = new int[size];
            var count = size;
            if (getgrouplist(name, primary, groups, ref count) == 0)
                return groups[..Math.Min(count, size)].Select(g => (uint)g).ToArray();
        }
        throw new InvalidOperationException("getgrouplist did not fit.");
    }

    /// <summary>The id of the <c>admin</c> group, or null when there is none.</summary>
    [SupportedOSPlatform("macos")]
    private static uint? AdminGroupId()
    {
        // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; char **gr_mem; }: gr_gid at offset 16 on 64-bit.
        var group = getgrnam(GroupName);
        return group == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(group, 2 * IntPtr.Size);
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc")]
    private static extern uint getegid();

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr getpwuid(uint uid);

    // macOS: int getgrouplist(const char *name, int basegid, int *groups, int *ngroups); -1 when the array is too small.
    [DllImport("libc")]
    private static extern int getgrouplist([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int baseGroup, [In, Out] int[] groups, ref int count);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr getgrnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
