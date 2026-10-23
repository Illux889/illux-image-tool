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
    /// <summary>
    /// Checks every existing folder on the way (before anything is created), creates the folder if needed and checks
    /// again. Returns null when it is safe, otherwise the reason it is not. No file in the folder is touched here.
    /// </summary>
    public static string? PrepareTrusted(string directory)
    {
        var full = Path.GetFullPath(directory);
        var problem = Verify(full);
        if (problem is not null) return problem;

        var info = new DirectoryInfo(full);
        if (!info.Exists) info.Create(CreateSecurity());

        problem = Verify(full);
        if (problem is not null) return problem;

        info.Refresh();
        info.SetAccessControl(CreateSecurity()); // also tightens a folder that already existed
        return null;
    }

    /// <summary>
    /// A reason the folder cannot be trusted, or null: any existing folder in its path (itself included) that is a
    /// link, or that is not owned by SYSTEM/Administrators. A folder that does not exist yet is not an error.
    /// </summary>
    public static string? Verify(string directory)
    {
        var existing = new List<DirectoryInfo>();
        for (var d = new DirectoryInfo(Path.GetFullPath(directory)); d is not null; d = d.Parent)
        {
            if (d.Exists) existing.Add(d);
        }

        foreach (var d in existing)
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return $"The update folder '{directory}' is (or sits below) a link ('{d.FullName}'); refusing to use it.";
        }

        foreach (var d in existing)
        {
            var owner = d.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !IsTrustedOwner(owner))
                return $"The folder '{d.FullName}' on the way to the update folder is not owned by SYSTEM or Administrators (owner: {owner?.Value ?? "unknown"}); refusing to use it.";
        }
        return null;
    }

    public static bool IsTrustedOwner(SecurityIdentifier owner) =>
        owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || owner.Value == TrustedInstallerSid;

    /// <summary>NT SERVICE\TrustedInstaller owns some system folders; it is as privileged as SYSTEM.</summary>
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

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
