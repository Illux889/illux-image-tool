using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>One line of the watched-folder list in <see cref="MainWindow"/>.</summary>
public sealed record FolderRow(string Name, string In, string Out, string Options, string On)
{
    public static FolderRow From(WatchedFolder folder) =>
        new(folder.Name, folder.In, folder.Out, string.Join(" ", folder.Parameters), folder.Enabled ? "Yes" : "No");
}
