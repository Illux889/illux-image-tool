using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Illux.ImageTool.Settings;

/// <summary>
/// Whether an account is listed in the local Administrators group (R43: a named service account must be a local
/// Administrator, or the service cannot install updates). Only direct members are seen: an account that is an
/// administrator through a domain group cannot be told apart cheaply, so the caller only warns.
/// </summary>
internal static class LocalAdministrators
{
    private const int NerrSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxPreferredLength = -1;

    /// <summary>True or false when it could be determined; null when the account or the group could not be read.</summary>
    public static bool? IsDirectMember(string account)
    {
        try
        {
            var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            var members = MemberSids();
            return members is null ? null : members.Contains(sid);
        }
        catch (SystemException) // not mapped, no domain controller, group unreadable, ...
        {
            return null;
        }
    }

    /// <summary>The SIDs listed in the local Administrators group (its name is localised, so it is found by SID).</summary>
    internal static HashSet<SecurityIdentifier>? MemberSids()
    {
        var group = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount)).Value;
        var groupName = group[(group.IndexOf('\\') + 1)..];

        var status = NetLocalGroupGetMembers(null, groupName, 0, out var buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);
        try
        {
            if (status is not (NerrSuccess or ErrorMoreData)) return null;
            var result = new HashSet<SecurityIdentifier>();
            for (var i = 0; i < read; i++)
            {
                var sidPointer = Marshal.ReadIntPtr(buffer, i * IntPtr.Size); // LOCALGROUP_MEMBERS_INFO_0 { PSID }
                result.Add(new SecurityIdentifier(sidPointer));
            }
            return result;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(
        string? serverName, string localGroupName, int level, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
