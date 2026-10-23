namespace Illux.ImageTool.Runtime;

/// <summary>Shared machine-wide locations (spec 11): config, logs, run locks and the update flag.</summary>
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

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string RunsDir => Path.Combine(Root, "runs");

    public static string UpdatingFlag => Path.Combine(Root, "updating.flag");
}
