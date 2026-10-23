using System.Reflection;

namespace Illux.ImageTool.Settings;

internal static class AppVersion
{
    /// <summary>The installed version: the informational version without a commit or pre-release suffix.</summary>
    public static Version Current { get; } = Parse(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static Version Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return new Version(0, 0, 0);
        var core = informationalVersion.Split('+', '-')[0].Trim();
        return Version.TryParse(core, out var version)
            ? new Version(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0))
            : new Version(0, 0, 0);
    }
}
