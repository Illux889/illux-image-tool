using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watcher;

/// <summary>
/// R31: the updater writes <c>updating.flag</c> before it starts the installer and the installer removes it again.
/// If the installer died, the flag would block every command-line run forever, so the service removes a flag that is
/// older than 15 minutes when no installer is running: the setup program on Windows, Apple's <c>installer</c> on macOS.
/// The service looks for such a flag every minute (<see cref="UpdateWorker"/>, final review I3).
/// </summary>
public static class StaleUpdatingFlag
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public const string InstallerProcessPrefix = "IlluxImageTool-Setup-";

    /// <summary>
    /// The daemon installs a pkg with <c>/usr/sbin/installer</c>. A process counts only by that executable path
    /// (<c>proc_pidpath</c>), so any user's program merely named <c>installer</c> cannot hold the flag forever.
    /// </summary>
    public const string MacInstallerPath = "/usr/sbin/installer";

    public static bool RemoveIfStale(TimeProvider clock, Func<bool> installerRunning, ILogger log)
    {
        try
        {
            var path = AppPaths.UpdatingFlag;
            if (!File.Exists(path)) return false;
            if (clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) <= MaxAge) return false;
            if (installerRunning()) return false;

            File.Delete(path);
            log.LogWarning("Removed a stale updating.flag left behind by an update that did not finish.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not check or remove updating.flag");
            return false;
        }
    }

    public static bool IsInstallerRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        if (IsInstallerProcess(process.ProcessName, windows: true)) return true;
                    }
                    else if (OperatingSystem.IsMacOS() && ProcessPath(process.Id) is { } path && IsInstallerProcess(path, windows: false))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception)
        {
            return true; // cannot tell: leave the flag alone
        }
    }

    /// <param name="nameOrPath">Windows: the process name. macOS: the executable's full path (<see cref="ProcessPath"/>).</param>
    internal static bool IsInstallerProcess(string nameOrPath, bool windows) => windows
        ? nameOrPath.StartsWith(InstallerProcessPrefix, StringComparison.OrdinalIgnoreCase)
        : string.Equals(nameOrPath, MacInstallerPath, StringComparison.Ordinal);

    /// <summary>The executable path of process <paramref name="pid"/> (libproc's proc_pidpath), or null when it cannot be read.</summary>
    [SupportedOSPlatform("macos")]
    internal static string? ProcessPath(int pid)
    {
        if (pid <= 0) return null;
        var buffer = new byte[ProcPidPathInfoMaxSize];
        var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    /// <summary>PROC_PIDPATHINFO_MAXSIZE (4 * MAXPATHLEN).</summary>
    private const int ProcPidPathInfoMaxSize = 4096;

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint bufferSize);
}
