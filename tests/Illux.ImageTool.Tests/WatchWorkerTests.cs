using System.Diagnostics;
using System.Net;
using System.Text;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watch;
using Illux.ImageTool.Watcher;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Illux.ImageTool.Tests;

public sealed class WatchWorkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-watchworker-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger _log = new();
    private readonly List<WatchWorker> _workers = [];

    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
            worker.Dispose();
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    /// <summary>The worker may be reading the file at that moment, which can make replacing it fail briefly.</summary>
    private void SaveConfig(WatcherConfig config)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ConfigStore.Save(config, ConfigPath);
                return;
            }
            catch (Exception ex) when (attempt < 50 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private WatchedFolder Folder(string name, bool enabled = true) => new()
    {
        Name = name,
        In = Path.Combine(_dir, name, "In"),
        Out = Path.Combine(_dir, name, "Out"),
        Error = Path.Combine(_dir, name, "Error"),
        Enabled = enabled,
    };

    private static bool Exists(string folder) => Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0;

    private async Task<WatchWorker> StartAsync(Func<WatchedFolder, FolderScanner>? scannerFactory = null)
    {
        var worker = new WatchWorker(_log, new WatchWorkerOptions
        {
            ConfigPath = ConfigPath,
            PollInterval = TimeSpan.FromMilliseconds(30),
            ScannerFactory = scannerFactory,
            IsUpdating = () => false,
        });
        _workers.Add(worker);
        await worker.StartAsync(CancellationToken.None);
        return worker;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Worker_PicksUpConfigChange()
    {
        var first = Folder("first");
        var second = Folder("second");
        SaveConfig(new WatcherConfig { Folders = [first] });
        await StartAsync();

        // The second folder is added while the worker runs; its file must then be processed.
        TestImages.Write(second.In, "late.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        SaveConfig(new WatcherConfig { Folders = [first, second] });

        Assert.True(await WaitUntil(() => Exists(second.Out)), "the file of the added folder was not processed");
        Assert.True(File.Exists(Path.Combine(second.Out, "late.jpg")));
    }

    [Fact]
    public async Task Worker_BusyWhileProcessing()
    {
        var folder = Folder("busy");
        TestImages.Write(folder.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        SaveConfig(new WatcherConfig { Folders = [folder] });
        using var inVerifier = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var worker = await StartAsync(f =>
        {
            var scanner = new FolderScanner(f, NullLogger.Instance, isUpdating: () => false);
            scanner.OutputVerifier = _ =>
            {
                inVerifier.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return null;
            };
            return scanner;
        });

        Assert.False(worker.Busy);
        Assert.True(await WaitUntil(() => inVerifier.IsSet), "processing never started");
        Assert.True(worker.Busy);

        release.Set();
        Assert.True(await WaitUntil(() => !worker.Busy && Exists(folder.Out)));
    }

    [Fact]
    public async Task Worker_DisabledFolder_IsNotScanned()
    {
        var off = Folder("off", enabled: false);
        var on = Folder("on");
        TestImages.Write(off.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        TestImages.Write(on.In, "b.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        SaveConfig(new WatcherConfig { Folders = [off, on] });

        await StartAsync();

        Assert.True(await WaitUntil(() => Exists(on.Out)));
        await Task.Delay(300);
        Assert.False(Directory.Exists(off.Out));
        Assert.True(File.Exists(Path.Combine(off.In, "a.jpg")));
    }

    [Fact]
    public async Task Worker_InvalidConfig_KeepsLastGoodAndLogsOnce()
    {
        var folder = Folder("kept");
        SaveConfig(new WatcherConfig { Folders = [folder] });
        await StartAsync();
        await Task.Delay(150);

        File.WriteAllText(ConfigPath, "{ this is not json");
        Assert.True(await WaitUntil(() => _log.Count(LogLevel.Error) >= 1));
        TestImages.Write(folder.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);

        Assert.True(await WaitUntil(() => Exists(folder.Out)), "the last good configuration should still be in use");
        await Task.Delay(300);
        Assert.Equal(1, _log.Count(LogLevel.Error));
    }

    [Fact]
    public async Task Worker_ThrowingScanner_KeepsWorkerAlive_AndLogsOnceWithFolderName()
    {
        // A NUL character in a path makes the scanner throw (ArgumentException) instead of reporting a problem.
        var bad = new WatchedFolder { Name = "broken-folder", In = "C:\\bad\0folder", Out = "C:\\bad\\out", Error = "C:\\bad\\err" };
        var good = Folder("good");
        TestImages.Write(good.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        SaveConfig(new WatcherConfig { Folders = [bad, good] });

        await StartAsync();

        Assert.True(await WaitUntil(() => Exists(good.Out)), "the other folder must keep working");
        await Task.Delay(400);
        Assert.Equal(1, _log.Count(LogLevel.Error, "broken-folder"));
    }

    [Fact]
    public async Task Worker_StopDuringScan_LeavesOriginalInIn()
    {
        var folder = Folder("stopping");
        var input = TestImages.Write(folder.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        SaveConfig(new WatcherConfig { Folders = [folder] });
        using var inVerifier = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var worker = await StartAsync(f =>
        {
            var scanner = new FolderScanner(f, NullLogger.Instance, isUpdating: () => false);
            scanner.OutputVerifier = _ =>
            {
                inVerifier.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                throw new OperationCanceledException(); // what a stop request does to the work in flight
            };
            return scanner;
        });
        Assert.True(await WaitUntil(() => inVerifier.IsSet));

        var stopping = worker.StopAsync(CancellationToken.None);
        release.Set();
        await stopping;

        Assert.True(File.Exists(input), "the original must stay in the In folder");
        Assert.False(Exists(folder.Out), "no half-finished output may be left");
        Assert.False(worker.Busy);
    }

    [Fact]
    public async Task Worker_AtStart_RemovesOldTempFoldersFromOut()
    {
        var folder = Folder("cleanup");
        Directory.CreateDirectory(folder.In);
        var stale = Directory.CreateDirectory(Path.Combine(folder.Out, ".illux-tmp-" + Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(stale.FullName, "half.jpg"), "left by a killed service");
        Directory.SetLastWriteTimeUtc(stale.FullName, DateTime.UtcNow - TimeSpan.FromHours(2));
        Directory.SetCreationTimeUtc(stale.FullName, DateTime.UtcNow - TimeSpan.FromHours(2));
        SaveConfig(new WatcherConfig { Folders = [folder] });

        await StartAsync();

        Assert.True(await WaitUntil(() => !Directory.Exists(stale.FullName)), "the stale temp folder was not removed");
    }

    [Fact]
    public async Task Worker_Reload_KeepsUnchangedScanner_AndDropsRemovedOrDisabledFolders()
    {
        var created = new Dictionary<string, int>();
        FolderScanner Create(WatchedFolder f)
        {
            lock (created) created[f.Name] = created.GetValueOrDefault(f.Name) + 1;
            return new FolderScanner(f, NullLogger.Instance, isUpdating: () => false);
        }
        int Created(string name)
        {
            lock (created) return created.GetValueOrDefault(name);
        }

        var a = Folder("a");
        var b = Folder("b");
        var c = Folder("c");
        SaveConfig(new WatcherConfig { Folders = [a, b] });
        await StartAsync(Create);
        Assert.True(await WaitUntil(() => Created("a") == 1 && Created("b") == 1));

        // a is unchanged, b is disabled, c is new
        TestImages.Write(b.In, "b1.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        TestImages.Write(c.In, "c1.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        b.Enabled = false;
        SaveConfig(new WatcherConfig { Folders = [a, b, c] });
        Assert.True(await WaitUntil(() => Exists(c.Out)));
        await Task.Delay(300);
        Assert.False(Directory.Exists(b.Out), "a disabled folder must not be scanned");
        Assert.Equal(1, Created("a"));
        Assert.Equal(1, Created("c"));

        // enabling b again needs a fresh scanner
        b.Enabled = true;
        SaveConfig(new WatcherConfig { Folders = [a, b, c] });
        Assert.True(await WaitUntil(() => Exists(b.Out)));
        Assert.Equal(2, Created("b"));
        Assert.Equal(1, Created("a"));
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public int Count(LogLevel level)
        {
            lock (_entries) return _entries.Count(e => e.Level == level);
        }

        public int Count(LogLevel level, string containing)
        {
            lock (_entries) return _entries.Count(e => e.Level == level && e.Message.Contains(containing, StringComparison.Ordinal));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries) _entries.Add((logLevel, message));
        }
    }
}

/// <summary>No real installer is started and no real HTTP request is made: both are fakes.</summary>
[Collection("AppRoot")]
public sealed class UpdateWorkerTests : AppRootTestBase, IDisposable
{
    private const string Host = "https://github.com/Illux889/illux-image-tool/releases/download/v9.9.9/";
    private static readonly byte[] InstallerBytes = Encoding.ASCII.GetBytes("pretend installer");
    private static readonly string InstallerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(InstallerBytes));

    private readonly List<UpdateWorker> _workers = [];
    private readonly List<string> _requests = [];
    private readonly List<(string Path, string Args)> _started = [];
    private readonly string _checkNowName = $@"Local\IlluxImageTool.Test.{Guid.NewGuid():N}.CheckNow";
    private readonly string _installNowName = $@"Local\IlluxImageTool.Test.{Guid.NewGuid():N}.InstallNow";
    private bool _releaseAvailable = true;
    private volatile bool _throwOnApi;

    public new void Dispose()
    {
        foreach (var worker in _workers)
        {
            try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { }
            worker.Dispose();
        }
        base.Dispose();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        lock (_requests) _requests.Add(url);
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
        {
            if (_throwOnApi) throw new InvalidOperationException("unexpected failure");
            if (!_releaseAvailable) return new HttpResponseMessage(HttpStatusCode.NotFound);
            const string json = $$"""
                {"tag_name":"v9.9.9","draft":false,"prerelease":false,"assets":[
                {"name":"IlluxImageTool-Setup-9.9.9.exe","browser_download_url":"{{Host}}IlluxImageTool-Setup-9.9.9.exe"},
                {"name":"IlluxImageTool-Setup-9.9.9.exe.sha256","browser_download_url":"{{Host}}IlluxImageTool-Setup-9.9.9.exe.sha256"}]}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
        }
        if (url.EndsWith(".sha256", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(InstallerHash) };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InstallerBytes) };
    }

    private int ApiRequests()
    {
        lock (_requests) return _requests.Count(r => r.StartsWith("https://api.github.com/", StringComparison.Ordinal));
    }

    private int InstallerDownloads()
    {
        lock (_requests) return _requests.Count(r => r.EndsWith(".exe", StringComparison.Ordinal));
    }

    private async Task<UpdateWorker> StartAsync(
        bool periodicChecks = true, TimeSpan? initialDelay = null, bool useConfigFile = false, TimeSpan? checkInterval = null)
    {
        var http = new HttpClient(new FakeHandler(Respond));
        var updater = new Updater(
            new ReleaseClient(http), http, new Version(1, 0, 0),
            () => false,
            (path, args) => { lock (_started) _started.Add((path, args)); return new Process(); },
            NullLogger.Instance,
            downloadDirectory: Path.Combine(Dir, "download"),
            recheckInterval: TimeSpan.FromMilliseconds(10),
            trustDownloadDirectory: true);
        var worker = new UpdateWorker(updater, NullLogger.Instance, new UpdateWorkerOptions
        {
            InitialDelay = initialDelay ?? TimeSpan.Zero,
            CheckInterval = checkInterval ?? TimeSpan.FromHours(6),
            SignalPollInterval = TimeSpan.FromMilliseconds(20),
            CheckNowSignal = _checkNowName,
            InstallNowSignal = _installNowName,
            PeriodicChecksEnabled = useConfigFile ? null : () => periodicChecks,
        });
        _workers.Add(worker);
        await worker.StartAsync(CancellationToken.None);
        return worker;
    }

    /// <summary>What the settings window and tray do; retried until the worker has created its events.</summary>
    private async Task Request(bool install)
    {
        var name = install ? _installNowName : _checkNowName;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                ServiceSignals.Signal(name);
                return;
            }
            catch (IOException) when (watch.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(20);
            }
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private int StartedCount()
    {
        lock (_started) return _started.Count;
    }

    [Fact]
    public async Task UpdateWorker_NewVersion_NoInstallWithoutRequest()
    {
        await StartAsync();

        Assert.True(await WaitUntil(() => UpdateStateStore.Load().AvailableVersion == "9.9.9"));
        await Task.Delay(400);

        Assert.Equal(0, InstallerDownloads());
        Assert.Equal(0, StartedCount());
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public async Task UpdateWorker_InstallRequest_TriggersInstall()
    {
        await StartAsync();
        Assert.True(await WaitUntil(() => UpdateStateStore.Load().AvailableVersion == "9.9.9"));

        await Request(install: true);

        Assert.True(await WaitUntil(() => StartedCount() == 1), "the installer was not started");
        Assert.False(Directory.Exists(Path.Combine(AppRoot, "requests"))); // no request files any more (R37)
        Assert.EndsWith("IlluxImageTool-Setup-9.9.9.exe", _started[0].Path);
        Assert.Contains("/VERYSILENT", _started[0].Args);
        Assert.Equal(1, ApiRequests()); // the release from the earlier check was reused
    }

    [Fact]
    public async Task UpdateWorker_InstallRequest_WithoutEarlierCheck_ChecksFirst()
    {
        await StartAsync(periodicChecks: false);
        await Task.Delay(200);
        Assert.Equal(0, ApiRequests());

        await Request(install: true);

        Assert.True(await WaitUntil(() => StartedCount() == 1));
        Assert.Equal(1, ApiRequests());
    }

    [Fact]
    public async Task UpdateWorker_InstallRequest_NoRelease_InstallsNothing()
    {
        _releaseAvailable = false;
        await StartAsync(periodicChecks: false);

        await Request(install: true);

        Assert.True(await WaitUntil(() => ApiRequests() == 1));
        await Task.Delay(200);
        Assert.Equal(0, InstallerDownloads());
        Assert.Equal(0, StartedCount());
    }

    [Fact]
    public async Task UpdateWorker_CheckNowRequest_ChecksEvenWhenPeriodicChecksAreOff()
    {
        await StartAsync(periodicChecks: false);
        await Task.Delay(200);
        Assert.Equal(0, ApiRequests());

        await Request(install: false);

        Assert.True(await WaitUntil(() => ApiRequests() == 1));
        await Task.Delay(300);
        Assert.Equal(1, ApiRequests()); // acted on once: the event resets itself
        Assert.Equal(0, StartedCount());
    }

    [Fact]
    public async Task UpdateWorker_FirstPeriodicCheck_WaitsForInitialDelay()
    {
        await StartAsync(initialDelay: TimeSpan.FromSeconds(3));

        await Task.Delay(300);
        Assert.Equal(0, ApiRequests());
        Assert.True(await WaitUntil(() => ApiRequests() == 1));
    }

    [Fact]
    public async Task UpdateWorker_UnexpectedCheckFailure_IsNotRetriedEveryLoop_AndWorkerSurvives()
    {
        _throwOnApi = true;
        await StartAsync();

        Assert.True(await WaitUntil(() => ApiRequests() == 1));
        await Task.Delay(500); // many loop iterations at a 20 ms flag poll
        Assert.Equal(1, ApiRequests());

        _throwOnApi = false;
        await Request(install: false);
        Assert.True(await WaitUntil(() => UpdateStateStore.Load().AvailableVersion == "9.9.9"), "the worker must still react to requests");
        Assert.Equal(2, ApiRequests());
    }

    [Fact]
    public async Task UpdateWorker_StoppedWorker_NoLongerListens()
    {
        var worker = await StartAsync(periodicChecks: false);
        await Request(install: false);
        Assert.True(await WaitUntil(() => ApiRequests() == 1));

        await worker.StopAsync(CancellationToken.None);

        // Nothing is queued for later: a request while the service is down fails at once and is never acted on.
        Assert.Throws<IOException>(() => ServiceSignals.Signal(_checkNowName));
    }

    [Fact]
    public async Task UpdateWorker_ConfigCheckForUpdatesFalse_NoPeriodicCheck()
    {
        ConfigStore.Save(new WatcherConfig { CheckForUpdates = false });

        await StartAsync(useConfigFile: true, checkInterval: TimeSpan.FromMilliseconds(100));

        await Task.Delay(600);
        Assert.Equal(0, ApiRequests());
    }

    [Fact]
    public async Task UpdateWorker_ConfigCheckForUpdatesTrue_ChecksPeriodically()
    {
        ConfigStore.Save(new WatcherConfig { CheckForUpdates = true });

        await StartAsync(useConfigFile: true, checkInterval: TimeSpan.FromMilliseconds(100));

        Assert.True(await WaitUntil(() => ApiRequests() >= 2));
    }

    [Fact]
    public async Task UpdateWorker_BrokenConfig_KeepsLastGoodSetting()
    {
        ConfigStore.Save(new WatcherConfig { CheckForUpdates = false });
        await StartAsync(useConfigFile: true, initialDelay: TimeSpan.FromMilliseconds(200), checkInterval: TimeSpan.FromMilliseconds(100));
        await Task.Delay(500); // the false setting has been read by now

        File.WriteAllText(AppPaths.ConfigFile, "{ not json");
        await Task.Delay(600);

        Assert.Equal(0, ApiRequests());
    }
}
