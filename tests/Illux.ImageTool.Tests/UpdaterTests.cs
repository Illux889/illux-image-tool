using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Microsoft.Extensions.Logging.Abstractions;

namespace Illux.ImageTool.Tests;

/// <summary>No real installer is ever started here: <c>startInstaller</c> is a fake and HTTP is a fake handler.</summary>
[Collection("AppRoot")]
public sealed class UpdaterTests : AppRootTestBase
{
    private const string InstallerHost = "https://github.com/Illux889/illux-image-tool/releases/download/v1.2.3/";
    private static readonly byte[] InstallerBytes = Encoding.ASCII.GetBytes("pretend installer, not a real exe");
    private static readonly string InstallerHash = Convert.ToHexString(SHA256.HashData(InstallerBytes)).ToLowerInvariant();

    private string DownloadDir => Path.Combine(Dir, "download");

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.ToString());
                UserAgents.Add(request.Headers.UserAgent.ToString());
            }
            return Task.FromResult(respond(request));
        }
    }

    private static string ReleaseJson(string tag = "v1.2.3", bool draft = false, bool prerelease = false, bool withChecksum = true, bool withInstaller = true)
    {
        var version = tag.TrimStart('v');
        var assets = new List<string>();
        if (withInstaller)
            assets.Add($$"""{"name":"IlluxImageTool-Setup-{{version}}.exe","browser_download_url":"{{InstallerHost}}IlluxImageTool-Setup-{{version}}.exe"}""");
        if (withChecksum)
            assets.Add($$"""{"name":"IlluxImageTool-Setup-{{version}}.exe.sha256","browser_download_url":"{{InstallerHost}}IlluxImageTool-Setup-{{version}}.exe.sha256"}""");
        assets.Add("""{"name":"source.zip","browser_download_url":"https://example.com/source.zip"}""");
        return $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":[{{string.Join(',', assets)}}]}""";
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static bool IsInstallerRequest(HttpRequestMessage request) =>
        request.RequestUri!.ToString().EndsWith(".exe", StringComparison.Ordinal);

    private static HttpResponseMessage Route(HttpRequestMessage request, string checksumBody)
    {
        var url = request.RequestUri!.ToString();
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal)) return Text(ReleaseJson());
        if (url.EndsWith(".sha256", StringComparison.Ordinal)) return Text(checksumBody);
        if (url.EndsWith(".exe", StringComparison.Ordinal)) return Bytes(InstallerBytes);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static ReleaseInfo Release(string version = "1.2.3") => new(
        Version.Parse(version),
        new Uri($"{InstallerHost}IlluxImageTool-Setup-{version}.exe"),
        new Uri($"{InstallerHost}IlluxImageTool-Setup-{version}.exe.sha256"));

    private sealed class Rig
    {
        public required FakeHandler Handler { get; init; }
        public required Updater Updater { get; init; }
        public List<(string Path, string Args)> Started { get; } = [];
        public Func<(string Path, string Args), Process?> OnStart { get; set; } = _ => new Process();
        public Func<bool> Busy { get; set; } = () => false;
    }

    private Rig NewRig(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string current = "1.0.0",
        TimeSpan? recheck = null,
        TimeSpan? giveUp = null)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        Rig? rig = null;
        var updater = new Updater(
            new ReleaseClient(http), http, Version.Parse(current),
            () => rig!.Busy(),
            (path, args) => { rig!.Started.Add((path, args)); return rig.OnStart((path, args)); },
            NullLogger.Instance,
            downloadDirectory: DownloadDir,
            recheckInterval: recheck ?? TimeSpan.FromMilliseconds(10),
            giveUpAfter: giveUp ?? TimeSpan.FromSeconds(5));
        rig = new Rig { Handler = handler, Updater = updater };
        return rig;
    }

    [Fact]
    public async Task Latest_ParsesTagAndAssets()
    {
        var handler = new FakeHandler(_ => Text(ReleaseJson("v2.10.4")));
        var client = new ReleaseClient(new HttpClient(handler));

        var info = await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(new Version(2, 10, 4), info.Version);
        Assert.Equal($"{InstallerHost}IlluxImageTool-Setup-2.10.4.exe", info.InstallerUrl.ToString());
        Assert.Equal($"{InstallerHost}IlluxImageTool-Setup-2.10.4.exe.sha256", info.ChecksumUrl.ToString());
        Assert.Equal("https://api.github.com/repos/Illux889/illux-image-tool/releases/latest", Assert.Single(handler.Requests));
        Assert.StartsWith("IlluxImageTool/", handler.UserAgents[0]);
    }

    [Fact]
    public async Task Latest_MissingAsset_Null()
    {
        var noChecksum = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(withChecksum: false)))));
        var noInstaller = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(withInstaller: false)))));

        Assert.Null(await noChecksum.GetLatestAsync(CancellationToken.None));
        Assert.Null(await noInstaller.GetLatestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta")]
    [InlineData("release-7")]
    public async Task Latest_UnparsableTag_Null(string tag)
    {
        var client = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(tag)))));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_DraftOrPrerelease_Null()
    {
        Assert.Null(await new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(draft: true))))).GetLatestAsync(CancellationToken.None));
        Assert.Null(await new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(prerelease: true))))).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_NoReleaseYet_Null_AndServerError_Throws()
    {
        var none = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text("{}", HttpStatusCode.NotFound))));
        var broken = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text("", HttpStatusCode.InternalServerError))));

        Assert.Null(await none.GetLatestAsync(CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => broken.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_NonHttpsAsset_Null()
    {
        var json = ReleaseJson().Replace("https://github.com", "http://github.com");
        var client = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(json))));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Check_SameVersion_NoAvailableVersion()
    {
        var rig = NewRig(r => Route(r, InstallerHash), current: "1.2.3");
        UpdateStateStore.Save(new UpdateState { AvailableVersion = "1.0.5" });

        var result = await rig.Updater.CheckAsync(CancellationToken.None);

        Assert.Null(result);
        var state = UpdateStateStore.Load();
        Assert.Null(state.AvailableVersion);
        Assert.NotNull(state.LastCheck);
    }

    [Fact]
    public async Task Check_OlderRelease_NotAvailable()
    {
        var rig = NewRig(r => Route(r, InstallerHash), current: "1.2.4");

        Assert.Null(await rig.Updater.CheckAsync(CancellationToken.None));
        Assert.Null(UpdateStateStore.Load().AvailableVersion);
    }

    [Fact]
    public async Task Check_FourPartCurrentVersion_SameReleaseIsNotNewer()
    {
        var rig = NewRig(r => Route(r, InstallerHash), current: "1.2.3.0");

        Assert.Null(await rig.Updater.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Check_Newer_SetsAvailableVersion_DoesNotDownloadOrStart()
    {
        var rig = NewRig(r => Route(r, InstallerHash), current: "1.0.0");

        var result = await rig.Updater.CheckAsync(CancellationToken.None);

        Assert.Equal(new Version(1, 2, 3), result!.Version);
        var state = UpdateStateStore.Load();
        Assert.Equal("1.2.3", state.AvailableVersion);
        Assert.NotNull(state.LastCheck);
        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.Single(rig.Handler.Requests); // only the API query, no installer or checksum request
        Assert.StartsWith("https://api.github.com/", rig.Handler.Requests[0]);
        Assert.Empty(rig.Started);
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
        Assert.False(Directory.Exists(DownloadDir));
    }

    [Fact]
    public async Task Check_NetworkFailure_KeepsStateAndReturnsNull()
    {
        var rig = NewRig(_ => throw new HttpRequestException("offline"));
        UpdateStateStore.Save(new UpdateState { AvailableVersion = "1.1.0" });

        Assert.Null(await rig.Updater.CheckAsync(CancellationToken.None));
        Assert.Equal("1.1.0", UpdateStateStore.Load().AvailableVersion);
    }

    [Fact]
    public async Task Install_ChecksumOk_StartsInstallerWithSilentArgs()
    {
        var rig = NewRig(r => Route(r, InstallerHash.ToUpperInvariant() + "  IlluxImageTool-Setup-1.2.3.exe\n"));
        byte[]? bytesSeenByInstaller = null;
        string? flagSeen = null;
        rig.OnStart = start =>
        {
            bytesSeenByInstaller = File.ReadAllBytes(start.Path);
            flagSeen = File.Exists(AppPaths.UpdatingFlag) ? "present" : "missing";
            return new Process();
        };

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Started, outcome);
        var started = Assert.Single(rig.Started);
        Assert.Equal(Path.Combine(DownloadDir, "IlluxImageTool-Setup-1.2.3.exe"), started.Path);
        Assert.Equal(
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{Path.Combine(AppPaths.LogsDir, "update-1.2.3.log")}\"",
            started.Args);
        Assert.Equal(InstallerBytes, bytesSeenByInstaller);
        Assert.Equal("present", flagSeen); // flag is written before the installer starts
        Assert.True(File.Exists(AppPaths.UpdatingFlag)); // the installer (Task 14) removes it
        Assert.Equal(UpdatePhase.Installing, UpdateStateStore.Load().Phase);
        Assert.Equal([Path.Combine(DownloadDir, "IlluxImageTool-Setup-1.2.3.exe")], Directory.GetFiles(DownloadDir));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not a hash at all")]
    [InlineData("")]
    [InlineData("abcdef")]
    public async Task Install_ChecksumMismatchOrMalformed_NotStarted_FileDeleted(string checksumBody)
    {
        var rig = NewRig(r => Route(r, checksumBody));

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.ChecksumFailed, outcome);
        Assert.Empty(rig.Started);
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
        var state = UpdateStateStore.Load();
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.False(string.IsNullOrWhiteSpace(state.Error));
    }

    [Fact]
    public async Task Install_ChecksumWithBomAndBinaryMarker_Accepted()
    {
        var rig = NewRig(r => Route(r, "﻿" + InstallerHash + " *IlluxImageTool-Setup-1.2.3.exe\r\n"));

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(Release(), CancellationToken.None));
    }

    [Fact]
    public async Task Install_ActiveRunLock_WaitsThenStarts()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        var held = RunLock.Acquire();
        var phaseWhileWaiting = "";
        var release = Task.Run(async () =>
        {
            await Task.Delay(200); // the run finishes while the updater is waiting
            phaseWhileWaiting = UpdateStateStore.Load().Phase;
            held.Dispose();
        });
        rig.OnStart = _ =>
        {
            Assert.False(RunLock.AnyActive()); // the installer must not start while a run lock is held
            return new Process();
        };

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        await release;
        Assert.Equal(InstallOutcome.Started, outcome);
        Assert.Equal(UpdatePhase.WaitingForJobs, phaseWhileWaiting);
        Assert.Single(rig.Started);
    }

    [Fact]
    public async Task Install_WatcherBusy_WaitsThenStarts()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        var polls = 0;
        rig.Busy = () => ++polls < 4;
        rig.OnStart = _ =>
        {
            Assert.Equal(4, polls);
            return new Process();
        };

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(Release(), CancellationToken.None));
    }

    [Fact]
    public async Task Install_JobsNeverFinish_GivesUpBusy_NothingLeftBehind()
    {
        var rig = NewRig(r => Route(r, InstallerHash), recheck: TimeSpan.FromMilliseconds(10), giveUp: TimeSpan.FromMilliseconds(120));
        rig.Busy = () => true;

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Busy, outcome);
        Assert.Empty(rig.Started);
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
        Assert.Empty(Directory.GetFiles(DownloadDir));
        var state = UpdateStateStore.Load();
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.Equal("Jobs kept running, try again", state.Error);
    }

    [Fact]
    public async Task Install_PhasesWrittenInOrder()
    {
        var phases = new List<string>();
        void Record() => phases.Add(UpdateStateStore.Load().Phase);
        var rig = NewRig(r =>
        {
            if (IsInstallerRequest(r)) Record(); // while downloading
            return Route(r, InstallerHash);
        });
        rig.Busy = () => { Record(); return false; };
        rig.OnStart = _ => { Record(); return new Process(); };

        await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(
            [UpdatePhase.Downloading, UpdatePhase.WaitingForJobs, UpdatePhase.Installing],
            phases);
    }

    [Fact]
    public async Task Install_KeepsAvailableVersionAndLastCheck()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        var stamp = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        UpdateStateStore.Save(new UpdateState { AvailableVersion = "1.2.3", LastCheck = stamp });

        await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        var state = UpdateStateStore.Load();
        Assert.Equal("1.2.3", state.AvailableVersion);
        Assert.Equal(stamp, state.LastCheck);
    }

    [Theory]
    [InlineData(true)]  // startInstaller returns null
    [InlineData(false)] // startInstaller throws
    public async Task Install_StartFails_FlagRemoved_Failed(bool returnsNull)
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        rig.OnStart = _ => returnsNull ? null : throw new InvalidOperationException("cannot start");

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Single(rig.Started);
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
        var state = UpdateStateStore.Load();
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.False(string.IsNullOrWhiteSpace(state.Error));
    }

    [Fact]
    public async Task Install_DownloadHttpError_Failed_NothingStarted()
    {
        var rig = NewRig(r => IsInstallerRequest(r) ? new HttpResponseMessage(HttpStatusCode.NotFound) : Route(r, InstallerHash));

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Empty(rig.Started);
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.Equal(UpdatePhase.Failed, UpdateStateStore.Load().Phase);
    }

    /// <summary>Delivers some bytes, then runs <c>afterFirstChunk</c> (which fails or cancels) in the middle of the download.</summary>
    private sealed class BrokenStream(Action afterFirstChunk) : Stream
    {
        private bool _sent;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent) { afterFirstChunk(); return 0; }
            _sent = true;
            var n = Math.Min(count, 10);
            Array.Fill(buffer, (byte)7, offset, n);
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent)
            {
                afterFirstChunk();
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(0);
            }
            _sent = true;
            var n = Math.Min(buffer.Length, 10);
            buffer.Span[..n].Fill(7);
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Install_DownloadBreaksHalfway_Failed_NoFileLeftBehind()
    {
        var rig = NewRig(r => IsInstallerRequest(r)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(() => throw new IOException("connection reset"))) }
            : Route(r, InstallerHash));

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Empty(rig.Started);
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.Contains("connection reset", UpdateStateStore.Load().Error);
    }

    [Fact]
    public async Task Install_CancelledDuringDownload_Throws_NoFileLeftBehind_StateIdle()
    {
        using var cts = new CancellationTokenSource();
        var rig = NewRig(r => IsInstallerRequest(r)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(cts.Cancel)) }
            : Route(r, InstallerHash));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Updater.InstallAsync(Release(), cts.Token));

        Assert.Empty(rig.Started);
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.Equal(UpdatePhase.Idle, UpdateStateStore.Load().Phase);
    }

    [Fact]
    public async Task Install_CancelledWhileWaiting_Throws_NoFileLeftBehind()
    {
        using var cts = new CancellationTokenSource();
        var rig = NewRig(r => Route(r, InstallerHash));
        rig.Busy = () => { cts.Cancel(); return true; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Updater.InstallAsync(Release(), cts.Token));

        Assert.Empty(rig.Started);
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public void Defaults_AreTenSecondsAndThirtyMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), Updater.DefaultRecheckInterval);
        Assert.Equal(TimeSpan.FromMinutes(30), Updater.DefaultGiveUpAfter);
    }

    [Fact]
    public void StateStore_RoundTrips_MissingAndCorruptFilesGiveDefault()
    {
        Assert.Equal(UpdatePhase.Idle, UpdateStateStore.Load().Phase);

        var stamp = new DateTimeOffset(2026, 10, 23, 12, 30, 0, TimeSpan.FromHours(2));
        UpdateStateStore.Save(new UpdateState { AvailableVersion = "3.1.4", LastCheck = stamp, Phase = UpdatePhase.Failed, Error = "boom" });
        var loaded = UpdateStateStore.Load();
        Assert.Equal("3.1.4", loaded.AvailableVersion);
        Assert.Equal(stamp, loaded.LastCheck);
        Assert.Equal(UpdatePhase.Failed, loaded.Phase);
        Assert.Equal("boom", loaded.Error);
        Assert.Equal([UpdateStateStore.FilePath], Directory.GetFiles(AppPaths.Root)); // no temp file left

        File.WriteAllText(UpdateStateStore.FilePath, "{ not json");
        Assert.Equal(UpdatePhase.Idle, UpdateStateStore.Load().Phase);
    }

    [Fact]
    public async Task StateStore_ConcurrentReadersNeverSeeTornFile()
    {
        UpdateStateStore.Save(new UpdateState { Phase = UpdatePhase.Downloading, AvailableVersion = "9.9.9" });
        using var stop = new CancellationTokenSource();
        var torn = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                if (UpdateStateStore.Load().AvailableVersion != "9.9.9") torn++; // a half-written file would load as the default
        });

        for (var i = 0; i < 200; i++)
            UpdateStateStore.Save(new UpdateState { Phase = i % 2 == 0 ? UpdatePhase.Installing : UpdatePhase.Downloading, AvailableVersion = "9.9.9" });
        stop.Cancel();
        await reader;

        Assert.Equal(0, torn);
    }
}
