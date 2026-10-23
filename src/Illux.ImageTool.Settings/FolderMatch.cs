using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

internal static class FolderMatch
{
    /// <summary>Finds the folder by Name and In after the config was re-read (the list index may have moved). -1 when it is gone.</summary>
    public static int IndexOf(IReadOnlyList<WatchedFolder> folders, string name, string inFolder)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            if (string.Equals(folders[i].Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(folders[i].In, inFolder, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
