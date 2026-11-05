using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Settings;

/// <summary>
/// Best-effort log of unexpected errors in the settings app: <c>settings-YYYYMMDD.log</c>. Never throws. On Windows an
/// elevated copy writes into the admin-only <c>service-logs\</c> (R38), never into <c>logs\</c>, where standard users can
/// plant links; the tray helper runs as the user and keeps using <c>logs\</c>. On macOS the app always runs as the user
/// and logs into that user's own <c>~/Library/Logs/Illux Image Tool/</c>, not into the shared (1777) <c>logs/</c>
/// (final review M1).
/// </summary>
internal static class SettingsLog
{
    public static string DirectoryFor(bool elevated) =>
        DirectoryFor(elevated, OperatingSystem.IsMacOS(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string DirectoryFor(bool elevated, bool mac, string home)
    {
        if (mac) return Path.Combine(home, "Library", "Logs", "Illux Image Tool");
        return elevated ? AppPaths.ServiceLogsDir : AppPaths.LogsDir;
    }

    public static string FileFor(DateTime now, bool elevated) => Path.Combine(DirectoryFor(elevated), $"settings-{now:yyyyMMdd}.log");

    /// <summary>On macOS the app always runs as the user (it has no elevated copy).</summary>
    public static void Write(string message, Exception? exception = null) =>
        Write(message, exception, OperatingSystem.IsWindows() && Elevation.IsElevated());

    public static void Write(string message, Exception? exception, bool elevated)
    {
        try
        {
            var now = DateTime.Now;
            Directory.CreateDirectory(DirectoryFor(elevated));
            File.AppendAllText(FileFor(now, elevated), $"{now:yyyy-MM-dd HH:mm:ss} {message}{(exception is null ? "" : " " + exception)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never take the tray down.
        }
    }
}
