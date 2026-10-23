using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Settings;

/// <summary>
/// Best-effort log of unexpected errors in the settings app: <c>settings-YYYYMMDD.log</c>. Never throws. An elevated
/// copy writes into the admin-only <c>service-logs\</c> (R38), never into <c>logs\</c>, where standard users can plant
/// links; the tray helper runs as the user and keeps using <c>logs\</c>.
/// </summary>
internal static class SettingsLog
{
    public static string DirectoryFor(bool elevated) => elevated ? AppPaths.ServiceLogsDir : AppPaths.LogsDir;

    public static string FileFor(DateTime now, bool elevated) => Path.Combine(DirectoryFor(elevated), $"settings-{now:yyyyMMdd}.log");

    public static void Write(string message, Exception? exception = null) => Write(message, exception, Elevation.IsElevated());

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
