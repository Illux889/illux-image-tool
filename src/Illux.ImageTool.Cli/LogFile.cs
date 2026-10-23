using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Cli;

/// <summary>Best-effort daily log file <c>logs\cli-YYYYMMDD.log</c>; a logging failure never affects a run.</summary>
internal static class LogFile
{
    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var path = Path.Combine(AppPaths.LogsDir, $"cli-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing sensible left to do.
        }
    }
}
