using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Batch;

/// <summary>
/// The temporary folder <see cref="FileProcessor"/> writes into, next to the destination (R46).
/// It is created in one step with a protected ACL: only SYSTEM, Administrators and the account running the tool may
/// change the folder, so nobody else can add a link or a file to it. Files created in it get the permissions they would
/// get in the output folder itself (its "files" entries are copied as inherit-only), so an output keeps the output
/// folder's permissions after it is moved into place. The watcher (SYSTEM) only removes an old temp folder that still
/// looks like this, and never follows a link while doing so.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TempFolder
{
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericExecute = (FileSystemRights)0x20000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights GenericRead = unchecked((FileSystemRights)0x80000000);

    /// <summary>Rights that let a principal add, remove, rename or re-permission entries of a folder.</summary>
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership | GenericWrite | GenericAll;

    /// <summary>
    /// Creates the folder with the protected ACL. When that is not possible (for example the output folder's permissions
    /// cannot be read on some network drives) it is created as an ordinary folder, which the watcher then never removes.
    /// </summary>
    public static void Create(string path)
    {
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(outputFolder);
        try
        {
            new DirectoryInfo(path).Create(CreateSecurity(outputFolder));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            Directory.CreateDirectory(path);
        }
    }

    /// <summary>Null when <paramref name="folder"/> looks like a folder made by <see cref="Create"/>, otherwise why not.</summary>
    public static string? WhyNotOurs(DirectoryInfo folder)
    {
        folder.Refresh();
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return "it is a link";

        var security = folder.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner))
            return $"it is owned by {owner?.Value ?? "nobody"}, not by SYSTEM, Administrators or this account";
        if (!security.AreAccessRulesProtected) return "its permissions are inherited from the output folder";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((rule.FileSystemRights & WriteRights) != 0 && !IsTrusted((SecurityIdentifier)rule.IdentityReference))
                return $"{rule.IdentityReference.Value} may change it";
        }
        return null;
    }

    /// <summary>
    /// Deletes <paramref name="root"/> depth first without following links: a link is removed itself and never entered,
    /// and each entry's attributes are read again right before it is deleted. Throws <see cref="IOException"/> (leaving
    /// the rest) when a folder on the way has become a link.
    /// </summary>
    public static void DeleteTree(string root)
    {
        DeleteContents(root, root);
        ThrowIfLinkOnTheWay(root, root);
        Directory.Delete(root, recursive: false);
    }

    private static void DeleteContents(string root, string folder)
    {
        ThrowIfLinkOnTheWay(root, folder);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteContents(root, entry);
                attributes = File.GetAttributes(entry);
            }

            ThrowIfLinkOnTheWay(root, folder);
            if (attributes.HasFlag(FileAttributes.Directory))
                Directory.Delete(entry, recursive: false); // removes a junction or folder link itself, never its target
            else
                File.Delete(entry); // removes a file link itself
        }
    }

    /// <summary>Every folder from <paramref name="root"/> down to <paramref name="folder"/> must still be a real folder.</summary>
    private static void ThrowIfLinkOnTheWay(string root, string folder)
    {
        for (var current = folder; ; current = Path.GetDirectoryName(current)!)
        {
            LinkGuard.ThrowIfLinkedFolder(current);
            if (current.Length <= root.Length) return;
        }
    }

    private static DirectorySecurity CreateSecurity(string outputFolder)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in TrustedSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        // What a file created directly in the output folder would get, for files only.
        var outputRules = new DirectoryInfo(outputFolder).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in outputRules)
        {
            if (!rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit)) continue;
            var rights = Specific(rule.FileSystemRights);
            if (rights == 0) continue;
            security.AddAccessRule(new FileSystemAccessRule(
                rule.IdentityReference, rights, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, rule.AccessControlType));
        }
        return security;
    }

    /// <summary>Maps generic rights (common on inherit-only entries) to the file rights they stand for.</summary>
    private static FileSystemRights Specific(FileSystemRights rights)
    {
        var result = rights & FileSystemRights.FullControl;
        if (rights.HasFlag(GenericRead)) result |= FileSystemRights.Read | FileSystemRights.Synchronize;
        if (rights.HasFlag(GenericWrite)) result |= FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;
        if (rights.HasFlag(GenericExecute)) result |= FileSystemRights.ExecuteFile | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;
        if (rights.HasFlag(GenericAll)) result |= FileSystemRights.FullControl;
        return result;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => TrustedSids().Contains(sid);

    private static SecurityIdentifier[] TrustedSids()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return
        [
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            identity.User!,
        ];
    }
}
