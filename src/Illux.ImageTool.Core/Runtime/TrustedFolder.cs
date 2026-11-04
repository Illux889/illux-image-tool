using System.Runtime.Versioning;

namespace Illux.ImageTool.Runtime;

/// <summary>
/// macOS: the one check for "a folder only its owner can change", read with lstat (a link is never followed). Used for
/// Root by the control socket and the config save (owner root), for every folder on the way to the update folder (root),
/// and by the folders job for its private work folder and ImageMagick's temp folder (owner: the folder account).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class TrustedFolder
{
    /// <summary>
    /// Null when <paramref name="path"/> is a real folder (no link) owned by <paramref name="owner"/> that group and
    /// others may not write to; otherwise why not, as a phrase that follows the folder's name ("is a link", ...).
    /// </summary>
    /// <exception cref="IOException">The folder cannot be looked at.</exception>
    public static string? Problem(string path, uint owner) => Check(path, owner).Reason;

    /// <summary>As <see cref="Problem"/>; <c>WrongOwner</c> tells a folder of another user from the other problems.</summary>
    public static (string? Reason, bool WrongOwner) Check(string path, uint owner)
    {
        if (UnixNative.LStat(path) is not { } status) return ("does not exist", false);
        if (status.Type == UnixNative.SymbolicLinkType) return ("is a link", false);
        if (status.Type != UnixNative.DirectoryType) return ("is not a folder", false);
        if (status.Owner != owner) return ($"is owned by user {status.Owner}, not {owner}", true);
        if ((status.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return ("is writable by group or others", false);
        return (null, false);
    }
}
