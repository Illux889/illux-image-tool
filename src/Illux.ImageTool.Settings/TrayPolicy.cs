using Illux.ImageTool.Update;

namespace Illux.ImageTool.Settings;

internal static class TrayPolicy
{
    /// <summary>
    /// The version the tray popup should offer now, or null: none offered, not newer than installed, the user chose "Later" for it,
    /// or it was already shown in this session.
    /// </summary>
    public static string? VersionToAnnounce(UpdateState state, Version installed, TrayMemory memory, string? alreadyShown)
    {
        // An update is already under way (started here or in the settings window): no popup on top of it.
        if (state.Phase is UpdatePhase.Downloading or UpdatePhase.WaitingForJobs or UpdatePhase.Installing) return null;

        var version = UpdateView.NewerVersion(state, installed)?.ToString();
        if (version is null) return null;
        if (string.Equals(version, alreadyShown, StringComparison.OrdinalIgnoreCase)) return null;
        return memory.IsLater(version) ? null : version;
    }
}
