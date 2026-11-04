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

    /// <summary>The macOS data folder (machine-wide, like ProgramData on Windows).</summary>
    public const string MacRoot = "/Library/Application Support/Illux Image Tool";

    /// <summary>
    /// <c>C:\ProgramData\Illux Image Tool</c> on Windows, <see cref="MacRoot"/> on macOS, unless
    /// <see cref="RootOverrideVariable"/> is set.
    /// </summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
            return !string.IsNullOrWhiteSpace(overridden)
                ? overridden
                : DefaultRoot(OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        }
    }

    internal static string DefaultRoot(bool windows, string commonApplicationData) =>
        windows ? Path.Combine(commonApplicationData, "Illux Image Tool") : MacRoot;

    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>The CLI's logs (<c>cli-YYYYMMDD.log</c>); users may write here.</summary>
    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>Logs of the watcher service and the elevated settings window: SYSTEM and Administrators only (R38).</summary>
    public static string ServiceLogsDir => Path.Combine(Root, "service-logs");

    public static string RunsDir => Path.Combine(Root, "runs");

    public static string UpdatingFlag => Path.Combine(Root, "updating.flag");

    /// <summary>macOS (V15): the account that runs the watched folders, one line, written by the package (root:wheel 0644).</summary>
    public static string FolderAccountFile => Path.Combine(Root, "folder-account");

    /// <summary>macOS (V15): the folders job's private work folder (owned by the folder account, 0700): ImageMagick's temp and the staging folders.</summary>
    public static string FolderWorkDir => Path.Combine(Root, "folder-work");

    /// <summary>macOS (V15): the folders job's own log (owned by the folder account, 0755).</summary>
    public static string FolderLogsDir => Path.Combine(Root, "folder-logs");
}
