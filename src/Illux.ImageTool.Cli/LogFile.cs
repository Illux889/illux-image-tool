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
            // The exception's lines are indented and cleaned: a file name in a message cannot forge an entry.
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Unexpected error:{Environment.NewLine}{LogText.Indented(exception.ToString())}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing sensible left to do.
        }
    }
}
