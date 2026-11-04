using System.Diagnostics;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>What the settings window does differently on Windows and macOS.</summary>
internal static class SettingsPlatform
{
    private static readonly Lazy<bool> MayChange = new(() => !OperatingSystem.IsMacOS() || MacAdminGroup.CurrentUserIsMember());

    /// <summary>Windows: the window runs elevated (R34). macOS: config.json is root:admin 0664, so only admin-group members.</summary>
    public static bool MayChangeSettings => MayChange.Value;

    /// <summary>Shown above a read-only window (macOS, not in the admin group).</summary>
    public const string ReadOnlyNote = "Read-only: only members of the admin group can change these settings.";

    /// <summary>Only Windows has a service account to change and a service log this window may read.</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>On macOS service-logs/ is root-only (0700): the window names it instead of reading it.</summary>
    public static string ServiceLogNote => $"The service log is admin-only: {AppPaths.ServiceLogsDir}";

    /// <summary>"Running", "Stopped", "Not installed", ... ; never throws. On macOS this starts launchctl: call it off the UI thread.</summary>
    public static string ServiceStatusText()
    {
        if (OperatingSystem.IsWindows()) return ServiceControl.GetStatusText();
        if (OperatingSystem.IsMacOS()) return MacServiceControl.GetStatusText();
        return "Unknown";
    }

    /// <summary>Windows: through a temporary file (R33). macOS: in place, Root is not writable for the app (V7).</summary>
    public static void SaveConfig(WatcherConfig config)
    {
        if (OperatingSystem.IsMacOS()) ConfigStore.SaveInPlace(config);
        else ConfigStore.Save(config);
    }

    /// <summary>Explorer or Finder on <paramref name="folder"/>; the path is one argument, never part of a shell line.</summary>
    public static ProcessStartInfo OpenFolderStartInfo(string folder)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(folder);
        return info;
    }
}
