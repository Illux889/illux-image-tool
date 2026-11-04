using System.Runtime.Versioning;
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

    // ----- macOS: the settings app writes the installer's config.json in place (V7) -----

    private const UnixFileMode Mode0664 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;

    [SupportedOSPlatform("macos")]
    private string InstalledConfig(string text = "{ \"pollSeconds\": 5 }")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, Mode0664);
        return path;
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void SaveInPlace_RoundTrips_KeepsTheFileAndItsMode_NoTempFile()
    {
        var path = InstalledConfig(new string(' ', 5000) + "{ \"pollSeconds\": 5 }"); // longer than the new text: truncated
        var config = new WatcherConfig
        {
            PollSeconds = 7,
            Folders = [new WatchedFolder { Name = "A", In = "/Users/Shared/A/In", Out = "/Users/Shared/A/Out", Error = "/Users/Shared/A/Error", Parameters = ["8bit"] }],
        };

        ConfigStore.SaveInPlace(config, path);

        var back = ConfigStore.Load(path);
        Assert.Equal(7, back.PollSeconds);
        Assert.Equal("/Users/Shared/A/In", Assert.Single(back.Folders).In);
        Assert.Equal(Options.ResolutionRules.Defaults, back.ResolutionRules);
        Assert.StartsWith("{", File.ReadAllText(path));
        Assert.Equal(Mode0664, File.GetUnixFileMode(path));
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void SaveInPlace_LinkAtTheConfigPath_IsRefused_TargetUntouched()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "elsewhere.json");
        File.WriteAllText(target, "precious");
        var path = Path.Combine(_dir, "config.json");
        File.CreateSymbolicLink(path, target);

        Assert.Throws<IOException>(() => ConfigStore.SaveInPlace(new WatcherConfig(), path));

        Assert.Equal("precious", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(path).LinkTarget);
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void SaveInPlace_HardLinkedFile_IsRefused_Untouched()
    {
        var path = InstalledConfig("precious");
        Assert.Equal(0, link(path, Path.Combine(_dir, "second-name")));

        Assert.Throws<IOException>(() => ConfigStore.SaveInPlace(new WatcherConfig(), path));

        Assert.Equal("precious", File.ReadAllText(path));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void SaveInPlace_MissingFile_IsRefused_NotCreated()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");

        var ex = Assert.Throws<IOException>(() => ConfigStore.SaveInPlace(new WatcherConfig(), path));

        Assert.Contains("missing", ex.Message);
        Assert.False(File.Exists(path));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void SaveInPlace_FolderAtTheConfigPath_IsRefused()
    {
        var path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<IOException>(() => ConfigStore.SaveInPlace(new WatcherConfig(), path));
        Assert.True(Directory.Exists(path));
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int link([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string existing,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string newName);
}
