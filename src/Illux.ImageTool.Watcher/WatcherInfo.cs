using System.Reflection;

namespace Illux.ImageTool.Watcher;

public static class WatcherInfo
{
    public const string ServiceName = "IlluxImageToolWatcher";

    public const string DisplayName = "Illux Image Tool Watcher";

    public const string EventLogSource = "Illux Image Tool";

    /// <summary>The running version: the assembly's informational version without the commit or pre-release suffix.</summary>
    public static Version CurrentVersion { get; } = ParseVersion(
        typeof(WatcherInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static Version ParseVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return new Version(0, 0, 0);
        var core = informationalVersion.Split('+', '-')[0].Trim();
        return Version.TryParse(core, out var version)
            ? new Version(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0))
            : new Version(0, 0, 0);
    }
}
