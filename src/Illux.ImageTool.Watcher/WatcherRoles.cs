namespace Illux.ImageTool.Watcher;

/// <summary>What one watcher process does (ruling V15).</summary>
public enum WatcherRole
{
    /// <summary>Windows: the single service runs the watched folders and the updates.</summary>
    All,

    /// <summary>macOS, the root job <c>dk.illux.imagetool.watcher</c>: the control socket and the updates, no watched folders.</summary>
    Updates,

    /// <summary>macOS, the job <c>dk.illux.imagetool.folders</c> that runs as the folder account: the watched folders only.</summary>
    Folders,
}

/// <summary>
/// The role from the command line. Windows takes no role argument (the one service does everything, as before V15).
/// On macOS the watcher must be started with exactly <c>--role updates</c> or <c>--role folders</c>; anything else is
/// refused, so a job set up wrongly never runs watched folders as root.
/// </summary>
public static class WatcherRoles
{
    public const string Argument = "--role";
    public const string UpdatesName = "updates";
    public const string FoldersName = "folders";

    /// <returns>The role, or null with <paramref name="error"/> saying why the arguments are refused.</returns>
    public static WatcherRole? Parse(IReadOnlyList<string> args, bool windows, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (windows)
        {
            error = args.Contains(Argument, StringComparer.OrdinalIgnoreCase)
                ? "The watcher service on Windows takes no role argument; it runs the watched folders and the updates."
                : null;
            return error is null ? WatcherRole.All : null;
        }

        if (args.Count == 2 && args[0] == Argument)
        {
            switch (args[1])
            {
                case UpdatesName:
                    error = null;
                    return WatcherRole.Updates;
                case FoldersName:
                    error = null;
                    return WatcherRole.Folders;
            }
        }
        error = $"Start the watcher with '{Argument} {UpdatesName}' (the root job: updates) or '{Argument} {FoldersName}' "
            + $"(the job of the folder account: watched folders); it was started with '{string.Join(' ', args)}'.";
        return null;
    }

    public static bool RunsFolders(WatcherRole role) => role is WatcherRole.All or WatcherRole.Folders;

    public static bool RunsUpdates(WatcherRole role) => role is WatcherRole.All or WatcherRole.Updates;

    public static string Name(WatcherRole role) => role switch
    {
        WatcherRole.Updates => UpdatesName,
        WatcherRole.Folders => FoldersName,
        _ => "all",
    };
}
