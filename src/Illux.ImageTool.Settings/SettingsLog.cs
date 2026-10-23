using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Settings;

/// <summary>Best-effort log of unexpected errors in the settings app: <c>logs\settings-YYYYMMDD.log</c>. Never throws.</summary>
internal static class SettingsLog
{
    public static string FileFor(DateTime now) => Path.Combine(AppPaths.LogsDir, $"settings-{now:yyyyMMdd}.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            var now = DateTime.Now;
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(FileFor(now), $"{now:yyyy-MM-dd HH:mm:ss} {message}{(exception is null ? "" : " " + exception)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never take the tray down.
        }
    }
}
