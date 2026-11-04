using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Update;

/// <summary>
/// The installer is run as SYSTEM (root on macOS), so the folder it is downloaded to must not be writable (or
/// replaceable by a link) by ordinary users.
/// Windows: it is created with an ACL for SYSTEM and Administrators only, and an existing folder is refused unless it
/// is a real folder owned by one of them.
/// macOS: it must lie below <see cref="AppPaths.Root"/>; every folder from Root down to it (both included) that exists
/// must be a real folder (no symbolic link) whose mode gives group and others no write access. It is created with
/// mode 0700, and an existing one is tightened to 0700.
/// </summary>
internal static class DownloadDirectory
{
    /// <summary>
    /// Checks every existing folder on the way (before anything is created), creates the folder if needed and checks
    /// again. Returns null when it is safe, otherwise the reason it is not. No file in the folder is touched here.
    /// </summary>
    public static string? PrepareTrusted(string directory) =>
        OperatingSystem.IsWindows() ? PrepareTrustedWindows(directory) : PrepareTrustedUnix(directory, AppPaths.Root);

    /// <summary>
    /// A reason the folder cannot be trusted, or null. A folder that does not exist yet is not an error.
    /// Windows: any existing folder in its path (itself included) that is a link, or that is not owned by
    /// SYSTEM/Administrators. macOS: see the class remarks.
    /// </summary>
    public static string? Verify(string directory) =>
        OperatingSystem.IsWindows() ? VerifyWindows(directory) : VerifyUnix(directory, AppPaths.Root);

    [SupportedOSPlatform("windows")]
    private static string? PrepareTrustedWindows(string directory)
    {
        var full = Path.GetFullPath(directory);
        var problem = VerifyWindows(full);
        if (problem is not null) return problem;

        var info = new DirectoryInfo(full);
        if (!info.Exists) info.Create(CreateSecurity());

        problem = VerifyWindows(full);
        if (problem is not null) return problem;

        info.Refresh();
        info.SetAccessControl(CreateSecurity()); // also tightens a folder that already existed
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? VerifyWindows(string directory)
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

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    [UnsupportedOSPlatform("windows")]
    private static string? PrepareTrustedUnix(string directory, string root)
    {
        var full = Path.GetFullPath(directory);
        var problem = VerifyUnix(full, root);
        if (problem is not null) return problem;

        if (!Directory.Exists(full)) Directory.CreateDirectory(full, OwnerOnly);

        problem = VerifyUnix(full, root);
        if (problem is not null) return problem;

        File.SetUnixFileMode(full, OwnerOnly); // also tightens a folder that already existed (checked above: not a link)
        return null;
    }

    [UnsupportedOSPlatform("windows")]
    private static string? VerifyUnix(string directory, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return $"The update folder '{directory}' is not below '{fullRoot}'; refusing to use it.";

        var chain = new List<string>();
        for (var d = full; d.Length >= fullRoot.Length; d = Path.GetDirectoryName(d)!)
        {
            chain.Add(d);
            if (d.Length == fullRoot.Length) break;
        }

        foreach (var path in chain)
        {
            var d = new DirectoryInfo(path);
            if (!d.Exists && !LinkGuard.IsLink(path)) continue; // a dangling link does not "exist" but is still a link
            if (LinkGuard.IsLink(d))
                return $"The update folder '{directory}' is (or sits below) a link ('{d.FullName}'); refusing to use it.";
            if ((d.UnixFileMode & GroupOrOtherWrite) != 0)
                return $"The folder '{d.FullName}' on the way to the update folder is writable by group or others; refusing to use it.";
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    public static bool IsTrustedOwner(SecurityIdentifier owner) =>
        owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || owner.Value == TrustedInstallerSid;

    /// <summary>NT SERVICE\TrustedInstaller owns some system folders; it is as privileged as SYSTEM.</summary>
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    [SupportedOSPlatform("windows")]
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
