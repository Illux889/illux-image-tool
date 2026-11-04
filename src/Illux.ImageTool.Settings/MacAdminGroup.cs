using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Illux.ImageTool.Settings;

/// <summary>
/// On macOS config.json is root:admin 0664: only members of the <c>admin</c> group may change the settings. Others
/// see the window read-only. Read from the process's own groups (getgroups/getegid) and the group database (getgrnam),
/// without starting another program.
/// </summary>
internal static class MacAdminGroup
{
    public const string GroupName = "admin";

    /// <summary>True when <paramref name="adminGroup"/> exists and is this process's effective or a supplementary group.</summary>
    public static bool IsMember(IReadOnlyCollection<uint> groups, uint effectiveGroup, uint? adminGroup) =>
        adminGroup is { } admin && (effectiveGroup == admin || groups.Contains(admin));

    /// <summary>Whether this process may write config.json; false when it cannot be told.</summary>
    [SupportedOSPlatform("macos")]
    public static bool CurrentUserIsMember()
    {
        try
        {
            return IsMember(CurrentGroups(), getegid(), AdminGroupId());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The supplementary groups of this process.</summary>
    [SupportedOSPlatform("macos")]
    private static uint[] CurrentGroups()
    {
        var count = getgroups(0, null);
        if (count <= 0) return [];
        var groups = new uint[count];
        count = getgroups(groups.Length, groups);
        if (count < 0) throw new InvalidOperationException($"getgroups failed (errno {Marshal.GetLastPInvokeError()}).");
        return groups[..count];
    }

    /// <summary>The id of the <c>admin</c> group, or null when there is none.</summary>
    [SupportedOSPlatform("macos")]
    private static uint? AdminGroupId()
    {
        // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; char **gr_mem; }: gr_gid at offset 16 on 64-bit.
        var group = getgrnam(GroupName);
        return group == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(group, 2 * IntPtr.Size);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getgroups(int size, [Out] uint[]? groups);

    [DllImport("libc")]
    private static extern uint getegid();

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr getgrnam([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
