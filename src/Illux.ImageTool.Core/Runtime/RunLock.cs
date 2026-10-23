namespace Illux.ImageTool.Runtime;

/// <summary>
/// Marks a running conversion so the updater can tell when the machine is idle. The lock is a file in
/// <see cref="AppPaths.RunsDir"/> held open exclusively; the operating system releases it if the process dies, which
/// leaves a stale file. Users may write in <c>runs\</c>, so the privileged side (<see cref="AnyActive"/>, called by
/// the service) only ever reads there (R39); stale files are removed by <see cref="Acquire"/>, which runs as the user.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _stream;

    private RunLock(FileStream stream) => _stream = stream;

    /// <summary>Takes a lock for the current process (removing stale ones first); dispose it when the run is over.</summary>
    public static RunLock Acquire()
    {
        Directory.CreateDirectory(AppPaths.RunsDir);
        RemoveStale();
        var path = Path.Combine(AppPaths.RunsDir, $"{Environment.ProcessId}-{Guid.NewGuid():N}.lock");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        return new RunLock(stream);
    }

    /// <summary>
    /// True when any lock file is currently held. Read-only: nothing is deleted or written, links are skipped, and a
    /// file that cannot be opened for another reason than a sharing violation does not count as a run.
    /// </summary>
    public static bool AnyActive()
    {
        try
        {
            foreach (var file in new DirectoryInfo(AppPaths.RunsDir).EnumerateFiles("*.lock"))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    // A live run holds the file with FileShare.None, so even a read-only open fails while it runs.
                    using var probe = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
                {
                    // Released between listing and probing, or not a lock a run of ours made (those inherit runs\'s ACL).
                }
                catch (IOException)
                {
                    return true; // sharing violation: a live run holds it
                }
            }
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // No runs\ folder (or it cannot be listed): no run of ours can hold a lock there.
        }
        return false;
    }

    /// <summary>Best effort, as the user: deletes lock files nobody holds. Links are left alone (a delete-on-close open would follow them).</summary>
    private static void RemoveStale()
    {
        try
        {
            foreach (var file in new DirectoryInfo(AppPaths.RunsDir).EnumerateFiles("*.lock"))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    using var probe = new FileStream(file.FullName, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held by a live run, or not ours to delete.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _stream.Dispose();
}
