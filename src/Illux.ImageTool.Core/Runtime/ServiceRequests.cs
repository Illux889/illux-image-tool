namespace Illux.ImageTool.Runtime;

/// <summary>
/// The client side of <see cref="IServiceSignals"/>, for the settings window and the tray / menu-bar app: sets the
/// service's named event on Windows (R37), sends one line to the daemon's control socket on macOS. Only a trigger; the
/// service does its own update check and never takes anything else from the request.
/// </summary>
public static class ServiceRequests
{
    internal const string NotRunningMessage = "The Illux Image Tool service is not running, so it cannot receive the request.";

    internal const string NotAllowedMessage = "This account may not send requests to the Illux Image Tool service.";

    /// <summary>Asks the service to look for an update now; false when the request could not be delivered.</summary>
    public static bool RequestCheckNow() => RequestCheckNow(out _);

    /// <summary>As <see cref="RequestCheckNow()"/>; <paramref name="error"/> says why it could not be delivered.</summary>
    public static bool RequestCheckNow(out string? error) => Send(install: false, out error);

    /// <summary>Asks the service to install the newer release it found. Only ever called from a user's click on "Update now".</summary>
    public static bool RequestInstallNow() => RequestInstallNow(out _);

    /// <summary>As <see cref="RequestInstallNow()"/>; <paramref name="error"/> says why it could not be delivered.</summary>
    public static bool RequestInstallNow(out string? error) => Send(install: true, out error);

    private static bool Send(bool install, out string? error)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ServiceSignals.Signal(install ? ServiceSignals.InstallNowName : ServiceSignals.CheckNowName);
            else if (OperatingSystem.IsMacOS())
                UnixSocketSignals.Send(AppPaths.Root, install ? UnixSocketSignals.InstallCommand : UnixSocketSignals.CheckCommand);
            else
                throw new IOException("Requests to the Illux Image Tool service are not supported on this system.");
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
