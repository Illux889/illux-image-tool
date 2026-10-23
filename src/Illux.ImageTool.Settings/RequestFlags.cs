using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Settings;

/// <summary>Asks the watcher service for something by dropping a flag file in <see cref="AppPaths.RequestsDir"/> (R29).</summary>
internal static class RequestFlags
{
    public static void RequestCheckNow() => Write(AppPaths.CheckNowFlag);

    /// <summary>Only ever called from a user's click on "Update now".</summary>
    public static void RequestInstallNow() => Write(AppPaths.InstallNowFlag);

    private static void Write(string path)
    {
        // The installer normally creates requests\ (users may write there); create it if it is missing.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("o"));
    }
}
