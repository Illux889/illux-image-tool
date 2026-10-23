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
        ConfigStore.Save(new WatcherConfig { Folders = [first] }, ConfigPath);
        await StartAsync();

        // The second folder is added while the worker runs; its file must then be processed.
        TestImages.Write(second.In, "late.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        ConfigStore.Save(new WatcherConfig { Folders = [first, second] }, ConfigPath);

        Assert.True(await WaitUntil(() => Exists(second.Out)), "the file of the added folder was not processed");
        Assert.True(File.Exists(Path.Combine(second.Out, "late.jpg")));
    }

    [Fact]
    public async Task Worker_BusyWhileProcessing()
    {
        var folder = Folder("busy");
        TestImages.Write(folder.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        ConfigStore.Save(new WatcherConfig { Folders = [folder] }, ConfigPath);
        using var inVerifier = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var worker = await StartAsync(f =>
        {
            var scanner = new FolderScanner(f, NullLogger.Instance);
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
        ConfigStore.Save(new WatcherConfig { Folders = [off, on] }, ConfigPath);

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
        ConfigStore.Save(new WatcherConfig { Folders = [folder] }, ConfigPath);
        await StartAsync();
        await Task.Delay(150);

        File.WriteAllText(ConfigPath, "{ this is not json");
        Assert.True(await WaitUntil(() => _log.Count(LogLevel.Error) >= 1));
        TestImages.Write(folder.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);

        Assert.True(await WaitUntil(() => Exists(folder.Out)), "the last good configuration should still be in use");
        await Task.Delay(300);
        Assert.Equal(1, _log.Count(LogLevel.Error));
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<LogLevel> _levels = [];

        public int Count(LogLevel level)
        {
            lock (_levels) return _levels.Count(l => l == level);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_levels) _levels.Add(logLevel);
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
    private bool _releaseAvailable = true;

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

    private async Task<UpdateWorker> StartAsync(bool periodicChecks = true, TimeSpan? initialDelay = null)
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
            CheckInterval = TimeSpan.FromHours(6),
            FlagPollInterval = TimeSpan.FromMilliseconds(20),
            PeriodicChecksEnabled = () => periodicChecks,
        });
        _workers.Add(worker);
        await worker.StartAsync(CancellationToken.None);
        return worker;
    }

    private static void DropFlag(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
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
    public async Task UpdateWorker_NewVersion_NoInstallWithoutFlag()
    {
        await StartAsync();

        Assert.True(await WaitUntil(() => UpdateStateStore.Load().AvailableVersion == "9.9.9"));
        await Task.Delay(400);

        Assert.Equal(0, InstallerDownloads());
        Assert.Equal(0, StartedCount());
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public async Task UpdateWorker_InstallFlag_TriggersInstall()
    {
        await StartAsync();
        Assert.True(await WaitUntil(() => UpdateStateStore.Load().AvailableVersion == "9.9.9"));

        DropFlag(AppPaths.InstallNowFlag);

        Assert.True(await WaitUntil(() => StartedCount() == 1), "the installer was not started");
        Assert.False(File.Exists(AppPaths.InstallNowFlag));
        Assert.EndsWith("IlluxImageTool-Setup-9.9.9.exe", _started[0].Path);
        Assert.Contains("/VERYSILENT", _started[0].Args);
        Assert.Equal(1, ApiRequests()); // the release from the earlier check was reused
    }

    [Fact]
    public async Task UpdateWorker_InstallFlag_WithoutEarlierCheck_ChecksFirst()
    {
        await StartAsync(periodicChecks: false);
        await Task.Delay(200);
        Assert.Equal(0, ApiRequests());

        DropFlag(AppPaths.InstallNowFlag);

        Assert.True(await WaitUntil(() => StartedCount() == 1));
        Assert.Equal(1, ApiRequests());
    }

    [Fact]
    public async Task UpdateWorker_InstallFlag_NoRelease_DeletesFlagAndInstallsNothing()
    {
        _releaseAvailable = false;
        await StartAsync(periodicChecks: false);

        DropFlag(AppPaths.InstallNowFlag);

        Assert.True(await WaitUntil(() => !File.Exists(AppPaths.InstallNowFlag)));
        await Task.Delay(200);
        Assert.Equal(0, InstallerDownloads());
        Assert.Equal(0, StartedCount());
    }

    [Fact]
    public async Task UpdateWorker_CheckNowFlag_ChecksEvenWhenPeriodicChecksAreOff()
    {
        await StartAsync(periodicChecks: false);
        await Task.Delay(200);
        Assert.Equal(0, ApiRequests());

        DropFlag(AppPaths.CheckNowFlag);

        Assert.True(await WaitUntil(() => ApiRequests() == 1));
        Assert.True(await WaitUntil(() => !File.Exists(AppPaths.CheckNowFlag)));
        Assert.Equal(0, StartedCount());
    }

    [Fact]
    public async Task UpdateWorker_FirstPeriodicCheck_WaitsForInitialDelay()
    {
        await StartAsync(initialDelay: TimeSpan.FromMilliseconds(600));

        await Task.Delay(250);
        Assert.Equal(0, ApiRequests());
        Assert.True(await WaitUntil(() => ApiRequests() == 1));
    }
}
