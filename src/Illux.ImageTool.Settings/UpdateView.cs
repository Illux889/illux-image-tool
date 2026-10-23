using Illux.ImageTool.Update;

namespace Illux.ImageTool.Settings;

/// <summary>What the settings window shows for one <see cref="UpdateState"/>.</summary>
internal sealed record UpdateView(bool ShowBanner, string BannerText, bool CanUpdate, string PhaseText, bool Busy, string? VersionText)
{
    public static UpdateView From(UpdateState state, Version installed, bool serviceRunning = true)
    {
        ArgumentNullException.ThrowIfNull(state);

        var busy = state.Phase is UpdatePhase.Downloading or UpdatePhase.WaitingForJobs or UpdatePhase.Installing;
        var phaseText = state.Phase switch
        {
            UpdatePhase.Downloading => "Downloading the update...",
            UpdatePhase.WaitingForJobs => "Waiting for running jobs to finish...",
            UpdatePhase.Installing => "Installing...",
            UpdatePhase.Failed => string.IsNullOrWhiteSpace(state.Error) ? "The update failed." : "The update failed: " + state.Error,
            _ => "",
        };

        var version = NewerVersion(state, installed);
        if (version is null) return new UpdateView(false, "", false, phaseText, busy, null);

        // Only the running service can act on the request flag.
        if (!serviceRunning && !busy && phaseText.Length == 0) phaseText = "The service is not running. Start it to update.";
        return new UpdateView(true, $"Version {version} is ready", !busy && serviceRunning, phaseText, busy, version.ToString());
    }

    /// <summary>The offered version, or null when none is offered or it is not newer than the installed one (stale state after an update).</summary>
    public static Version? NewerVersion(UpdateState state, Version installed)
    {
        if (!Version.TryParse(state.AvailableVersion, out var available)) return null;
        return available > installed ? available : null;
    }
}
