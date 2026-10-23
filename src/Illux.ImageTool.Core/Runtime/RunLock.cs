namespace Illux.ImageTool.Runtime;

/// <summary>
/// Marks a running conversion so the updater can tell when the machine is idle. The lock is a file in
/// <see cref="AppPaths.RunsDir"/> held open exclusively; the operating system releases it if the process dies,
/// which leaves a stale file that <see cref="AnyActive"/> recognises (it can be opened) and removes.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _stream;

    private RunLock(FileStream stream) => _stream = stream;

    /// <summary>Takes a lock for the current process; dispose it when the run is over.</summary>
    public static RunLock Acquire()
    {
        Directory.CreateDirectory(AppPaths.RunsDir);
        var path = Path.Combine(AppPaths.RunsDir, $"{Environment.ProcessId}-{Guid.NewGuid():N}.lock");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new RunLock(stream);
    }

    /// <summary>True when any lock file is currently held. Stale files (not held by anyone) are deleted.</summary>
    public static bool AnyActive()
    {
        if (!Directory.Exists(AppPaths.RunsDir)) return false;

        var active = false;
        foreach (var file in Directory.EnumerateFiles(AppPaths.RunsDir, "*.lock"))
        {
            try
            {
                // Opening succeeds only when nobody holds the file; DeleteOnClose then removes the stale file.
                using var probe = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (FileNotFoundException)
            {
                // Released between listing and probing.
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
                active = true; // sharing violation: a live run holds it
            }
            catch (UnauthorizedAccessException)
            {
                active = true; // cannot prove it is stale, so treat it as busy
            }
        }
        return active;
    }

    public void Dispose() => _stream.Dispose();
}
