namespace Illux.ImageTool.Runtime;

/// <summary>
/// The service's side of "Check for updates now" and "Update now" (R37): named events on Windows
/// (<see cref="ServiceSignals"/>), a control socket on macOS (<see cref="UnixSocketSignals"/>). A request is only a
/// trigger; the service decides everything else itself. Each request is auto-reset: several before the service looked
/// count as one, and none is kept while the service is not running. Clients use <see cref="ServiceRequests"/>.
/// </summary>
public interface IServiceSignals : IDisposable
{
    /// <summary>True (once) when "check now" was requested since the last look; waits at most <paramref name="timeout"/>.</summary>
    bool TryWaitCheckNow(TimeSpan timeout);

    /// <summary>True (once) when "update now" was requested since the last look; waits at most <paramref name="timeout"/>.</summary>
    bool TryWaitInstallNow(TimeSpan timeout);
}
