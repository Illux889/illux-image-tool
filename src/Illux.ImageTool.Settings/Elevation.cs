using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Illux.ImageTool.Settings;

/// <summary>
/// config.json is admin-only (R34), so the settings window runs elevated. The tray helper and the elevated service
/// copy never self-elevate: the tray only signals the service (R37), the service copy is already elevated.
/// </summary>
internal static class Elevation
{
    public static bool ShouldSelfElevate(string[] args, bool isElevated)
    {
        if (isElevated) return false;
        return !args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)
            || a.Equals(ServiceControl.ServiceArgument, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts this exe again with the Windows permission prompt. False when the user declined it.</summary>
    public static bool RelaunchElevated()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The program path is unknown.");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }
}
