using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Tests;

public sealed class ConfigStoreSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-cfgsave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Save_RetriesWhileAReaderHasTheFileOpen()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new WatcherConfig { PollSeconds = 5 }, path);

        // The service has config.json open without delete sharing; replacing it fails until the reader lets go.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () => { await Task.Delay(200); reader.Dispose(); });

        ConfigStore.Save(new WatcherConfig { PollSeconds = 9 }, path);
        await release;

        Assert.Equal(9, ConfigStore.Load(path).PollSeconds);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public void Save_GivesUpWhenTheFileStaysLocked()
    {
        var path = Path.Combine(_dir, "config.json");
        ConfigStore.Save(new WatcherConfig { PollSeconds = 5 }, path);

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Record.Exception(() => ConfigStore.Save(new WatcherConfig { PollSeconds = 9 }, path));
        Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected: {ex}");
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray()); // temp file cleaned up
    }
}
