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
        var version = UpdateView.NewerVersion(state, installed)?.ToString();
        if (version is null) return null;
        if (string.Equals(version, alreadyShown, StringComparison.OrdinalIgnoreCase)) return null;
        return memory.IsLater(version) ? null : version;
    }
}
