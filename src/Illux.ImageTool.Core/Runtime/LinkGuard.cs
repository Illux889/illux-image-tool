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

    /// <summary>True when <paramref name="path"/> exists and is a reparse point (the link itself is looked at, not its target).</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
