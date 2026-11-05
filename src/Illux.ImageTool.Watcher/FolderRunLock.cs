using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watcher;

/// <summary>
/// The run marker of the macOS folders job (V15): a <see cref="RunLock"/> in <c>runs/</c> (1777), taken as the folder
/// account like the CLI's, so the updater in the root job sees watched-folder work. A lock that cannot be taken is
/// logged once and the file is processed anyway (as in the CLI).
/// </summary>
public sealed class FolderRunLock(ILogger log)
{
    private int _failureLogged;

    public IDisposable? TryAcquire()
    {
        try
        {
            return RunLock.Acquire();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
                log.LogWarning("Could not take a run lock in {Runs}; an update may not wait for watched-folder work: {Message}", AppPaths.RunsDir, ex.Message);
            return null;
        }
    }
}
