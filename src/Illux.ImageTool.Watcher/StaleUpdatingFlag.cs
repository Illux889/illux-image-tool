using System.Diagnostics;
using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watcher;

/// <summary>
/// R31: the updater writes <c>updating.flag</c> before it starts the installer and the installer removes it again.
/// If the installer died, the flag would block every command-line run forever, so the service removes a flag that is
/// older than 15 minutes when no installer is running.
/// </summary>
public static class StaleUpdatingFlag
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public const string InstallerProcessPrefix = "IlluxImageTool-Setup-";

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
                    if (process.ProcessName.StartsWith(InstallerProcessPrefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
        catch (Exception)
        {
            return true; // cannot tell: leave the flag alone
        }
    }
}
