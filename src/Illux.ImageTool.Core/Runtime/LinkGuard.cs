namespace Illux.ImageTool.Runtime;

/// <summary>
/// The watcher runs as SYSTEM and writes into folders whose parents users can often change. Just before writing it
/// refuses a folder it created below a configured folder, or an existing file, that has become a link (junction or
/// symbolic link), so a user cannot redirect the write elsewhere (R44). The configured folder itself is the
/// administrator's choice and may be a link. A swap between this check and the write remains possible (accepted).
/// </summary>
public static class LinkGuard
{
    /// <summary>Throws <see cref="IOException"/> when <paramref name="folder"/> exists and is a link.</summary>
    public static void ThrowIfLinkedFolder(string folder)
    {
        if (IsLink(folder)) throw new IOException($"The folder '{folder}' has been replaced by a link; refusing to write there.");
    }

    /// <summary>Throws <see cref="IOException"/> when <paramref name="file"/> exists and is a link.</summary>
    public static void ThrowIfLinkedFile(string file)
    {
        if (IsLink(file)) throw new IOException($"'{file}' is a link; refusing to write through it.");
    }

    /// <summary>
    /// True when <paramref name="path"/> exists and is a link (the link itself is looked at, not its target): a reparse
    /// point on Windows, a symbolic link on macOS (.NET reports those as reparse points too; the link target is checked as well).
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return true;
            return !OperatingSystem.IsWindows() && new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>True when the entry (as listed, not followed) is a link; see <see cref="IsLink(string)"/>.</summary>
    public static bool IsLink(FileSystemInfo entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || (!OperatingSystem.IsWindows() && entry.LinkTarget is not null);
}
