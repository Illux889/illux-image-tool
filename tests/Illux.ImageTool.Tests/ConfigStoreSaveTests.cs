using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Tests;

public sealed class ConfigStoreSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-cfgsave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [WindowsFact] // Windows file sharing; on macOS a reader never blocks the replace
    public void Save_RetriesWhileAReaderHasTheFileOpen()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new WatcherConfig { PollSeconds = 5 }, path);

        // The service has config.json open without delete sharing; replacing it fails until the reader lets go.
        // The reader is released after the second failed attempt, so the test does not depend on timing.
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failedAttempts = 0;

        ConfigStore.Save(new WatcherConfig { PollSeconds = 9 }, path, onRetry: attempt =>
        {
            failedAttempts = attempt;
            if (attempt == 2) reader.Dispose();
        });

        Assert.Equal(2, failedAttempts);
        Assert.Equal(9, ConfigStore.Load(path).PollSeconds);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [WindowsFact]
    public void Save_GivesUpWhenTheFileStaysLocked()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new WatcherConfig { PollSeconds = 5 }, path);

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Record.Exception(() => ConfigStore.Save(new WatcherConfig { PollSeconds = 9 }, path));
        Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected: {ex}");
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray()); // temp file cleaned up
    }

    [MacFact]
    public void Save_OnMac_AReaderDoesNotBlockTheReplace()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new WatcherConfig { PollSeconds = 5 }, path);

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failedAttempts = 0;
        ConfigStore.Save(new WatcherConfig { PollSeconds = 9 }, path, onRetry: attempt => failedAttempts = attempt);

        Assert.Equal(0, failedAttempts);
        Assert.Equal(9, ConfigStore.Load(path).PollSeconds);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray());
    }
}
