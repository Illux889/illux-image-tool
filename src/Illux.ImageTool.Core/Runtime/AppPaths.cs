namespace Illux.ImageTool.Runtime;

/// <summary>Shared machine-wide locations (spec 11): config, logs, run locks and the update flag.</summary>
/// <remarks>
/// Standard users may write in <see cref="LogsDir"/> and <see cref="RunsDir"/> (the CLI runs as the AE user). A
/// privileged process (the service, the elevated settings window) never creates, opens for writing or deletes a file
/// by name there (R37-R39): its own logs go to <see cref="ServiceLogsDir"/>.
/// </remarks>
public static class AppPaths
{
    /// <summary>Environment variable that moves <see cref="Root"/>; used by tests.</summary>
    public const string RootOverrideVariable = "ILLUX_IMAGETOOL_ROOT";

    /// <summary><c>C:\ProgramData\Illux Image Tool</c>, unless <see cref="RootOverrideVariable"/> is set.</summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Illux Image Tool");
        }
    }

    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>The CLI's logs (<c>cli-YYYYMMDD.log</c>); users may write here.</summary>
    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>Logs of the watcher service and the elevated settings window: SYSTEM and Administrators only (R38).</summary>
    public static string ServiceLogsDir => Path.Combine(Root, "service-logs");

    public static string RunsDir => Path.Combine(Root, "runs");

    public static string UpdatingFlag => Path.Combine(Root, "updating.flag");
}
