using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Illux.ImageTool.Update;

/// <summary>
/// The installer is run as SYSTEM, so the folder it is downloaded to must not be writable (or replaceable by a
/// junction) by ordinary users: it is created with an ACL for SYSTEM and Administrators only, and an existing folder
/// is refused unless it is a real folder owned by one of them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DownloadDirectory
{
    /// <summary>Creates the folder if needed and checks it. Returns null when it is safe, otherwise the reason it is not.</summary>
    public static string? PrepareTrusted(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) info.Create(CreateSecurity());

        var problem = Verify(directory);
        if (problem is not null) return problem;

        info.Refresh();
        info.SetAccessControl(CreateSecurity()); // also tightens a folder that already existed
        return null;
    }

    /// <summary>A reason the folder cannot be trusted (link in its path, foreign owner), or null.</summary>
    public static string? Verify(string directory)
    {
        var info = new DirectoryInfo(directory);
        for (var d = info; d is not null; d = d.Parent)
        {
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"The update folder '{directory}' is (or sits below) a link ('{d.FullName}'); refusing to download there.";
        }

        if (!info.Exists) return $"The update folder '{directory}' does not exist.";
        var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && IsTrustedOwner(owner)
            ? null
            : $"The update folder '{directory}' is not owned by SYSTEM or Administrators (owner: {owner?.Value ?? "unknown"}); refusing to download there.";
    }

    public static bool IsTrustedOwner(SecurityIdentifier owner) =>
        owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }
}
