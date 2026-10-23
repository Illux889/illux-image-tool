using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Settings;

/// <summary>Asks the watcher service for something by setting one of its named events (R37).</summary>
/// <remarks>Throws <see cref="IOException"/> when the service is not running, <see cref="UnauthorizedAccessException"/> when this account may not signal it.</remarks>
internal static class ServiceRequests
{
    public static void RequestCheckNow() => ServiceSignals.Signal(ServiceSignals.CheckNowName);

    /// <summary>Only ever called from a user's click on "Update now".</summary>
    public static void RequestInstallNow() => ServiceSignals.Signal(ServiceSignals.InstallNowName);
}
