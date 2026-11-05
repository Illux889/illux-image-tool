using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Microsoft.Extensions.Logging;
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

    private const string MacPkg = "IlluxImageTool-1.2.3-macOS-arm64.pkg";

    private static string ReleaseJson(
        string tag = "v1.2.3", bool draft = false, bool prerelease = false, bool withChecksum = true, bool withInstaller = true,
        bool withMacPkg = false, bool withMacChecksum = true)
    {
        var version = tag.TrimStart('v');
        var assets = new List<string>();
        if (withInstaller)
            assets.Add($$"""{"name":"IlluxImageTool-Setup-{{version}}.exe","browser_download_url":"{{InstallerHost}}IlluxImageTool-Setup-{{version}}.exe"}""");
        if (withChecksum)
            assets.Add($$"""{"name":"IlluxImageTool-Setup-{{version}}.exe.sha256","browser_download_url":"{{InstallerHost}}IlluxImageTool-Setup-{{version}}.exe.sha256"}""");
        if (withMacPkg)
            assets.Add($$"""{"name":"IlluxImageTool-{{version}}-macOS-arm64.pkg","browser_download_url":"{{InstallerHost}}IlluxImageTool-{{version}}-macOS-arm64.pkg"}""");
        if (withMacPkg && withMacChecksum)
            assets.Add($$"""{"name":"IlluxImageTool-{{version}}-macOS-arm64.pkg.sha256","browser_download_url":"{{InstallerHost}}IlluxImageTool-{{version}}-macOS-arm64.pkg.sha256"}""");
        assets.Add("""{"name":"source.zip","browser_download_url":"https://example.com/source.zip"}""");
        return $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"assets":[{{string.Join(',', assets)}}]}""";
    }

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static bool IsInstallerRequest(HttpRequestMessage request) =>
        request.RequestUri!.ToString().EndsWith(".exe", StringComparison.Ordinal)
        || request.RequestUri!.ToString().EndsWith(".pkg", StringComparison.Ordinal);

    private static HttpResponseMessage Route(HttpRequestMessage request, string checksumBody)
    {
        var url = request.RequestUri!.ToString();
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal)) return Text(ReleaseJson());
        if (url.EndsWith(".sha256", StringComparison.Ordinal)) return Text(checksumBody);
        if (url.EndsWith(".exe", StringComparison.Ordinal) || url.EndsWith(".pkg", StringComparison.Ordinal)) return Bytes(InstallerBytes);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static ReleaseInfo Release(string version = "1.2.3") => new(
        Version.Parse(version),
        new Uri($"{InstallerHost}IlluxImageTool-Setup-{version}.exe"),
        new Uri($"{InstallerHost}IlluxImageTool-Setup-{version}.exe.sha256"));

    private static ReleaseInfo MacRelease() => new(
        new Version(1, 2, 3), new Uri($"{InstallerHost}{MacPkg}"), new Uri($"{InstallerHost}{MacPkg}.sha256"));

    private static ReleaseClient WindowsClient(HttpClient http) => new(http, ReleasePlatform.WindowsX64);

    private sealed class Rig
    {
        public required FakeHandler Handler { get; init; }
        public required Updater Updater { get; init; }
        public List<(string Path, string Args)> Started { get; } = [];
        public List<ProcessStartInfo> StartInfos { get; } = [];
        public Func<(string Path, string Args), Process?> OnStart { get; set; } = _ => new Process();
        public Func<bool> Busy { get; set; } = () => false;
    }

    private Rig NewRig(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string current = "1.0.0",
        TimeSpan? recheck = null,
        TimeSpan? giveUp = null,
        TimeProvider? time = null,
        ILogger? log = null,
        bool trust = true,
        long maxInstallerBytes = Updater.DefaultMaxInstallerBytes,
        string? downloadDirectory = null,
        ReleasePlatform platform = ReleasePlatform.WindowsX64,
        uint? downloadDirectoryOwner = null)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler);
        Rig? rig = null;
        var updater = new Updater(
            new ReleaseClient(http, platform), http, Version.Parse(current),
            () => rig!.Busy(),
            start =>
            {
                rig!.StartInfos.Add(start);
                rig.Started.Add((start.FileName, start.Arguments));
                return rig.OnStart((start.FileName, start.Arguments));
            },
            log ?? NullLogger.Instance,
            timeProvider: time,
            downloadDirectory: downloadDirectory ?? DownloadDir,
            recheckInterval: recheck ?? TimeSpan.FromMilliseconds(10),
            giveUpAfter: giveUp ?? TimeSpan.FromSeconds(5),
            trustDownloadDirectory: trust,
            maxInstallerBytes: maxInstallerBytes)
        {
            DownloadDirectoryOwner = downloadDirectoryOwner ?? DownloadDirectory.RootUserId,
        };
        rig = new Rig { Handler = handler, Updater = updater };
        return rig;
    }

    [Fact]
    public async Task Latest_ParsesTagAndAssets()
    {
        var handler = new FakeHandler(_ => Text(ReleaseJson("v2.10.4")));
        var client = WindowsClient(new HttpClient(handler));

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
        var noChecksum = WindowsClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(withChecksum: false)))));
        var noInstaller = WindowsClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(withInstaller: false)))));

        Assert.Null(await noChecksum.GetLatestAsync(CancellationToken.None));
        Assert.Null(await noInstaller.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_WithMacAssets_WindowsStillResolves()
    {
        var client = WindowsClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(withMacPkg: true)))));

        var info = await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 3), info.Version);
        Assert.Equal($"{InstallerHost}IlluxImageTool-Setup-1.2.3.exe", info.InstallerUrl.ToString());
        Assert.Equal($"{InstallerHost}IlluxImageTool-Setup-1.2.3.exe.sha256", info.ChecksumUrl.ToString());
    }

    [Fact]
    public async Task Latest_Mac_PicksArm64Pkg()
    {
        var client = new ReleaseClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(withMacPkg: true)))), ReleasePlatform.MacOSArm64);

        var info = await client.GetLatestAsync(CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 3), info.Version);
        Assert.Equal($"{InstallerHost}{MacPkg}", info.InstallerUrl.ToString());
        Assert.Equal($"{InstallerHost}{MacPkg}.sha256", info.ChecksumUrl.ToString());
        Assert.Equal(ReleasePlatform.MacOSArm64, client.Platform);
    }

    [Fact]
    public async Task Latest_Mac_MissingPkg_Null()
    {
        ReleaseClient Mac(string json) => new(new HttpClient(new FakeHandler(_ => Text(json))), ReleasePlatform.MacOSArm64);

        Assert.Null(await Mac(ReleaseJson()).GetLatestAsync(CancellationToken.None)); // Windows assets only
        Assert.Null(await Mac(ReleaseJson(withMacPkg: true, withMacChecksum: false)).GetLatestAsync(CancellationToken.None));
        Assert.Null(await Mac(ReleaseJson(withMacPkg: true).Replace("https://github.com/", "https://evil.example.com/"))
            .GetLatestAsync(CancellationToken.None)); // host allow-list unchanged
    }

    [Fact]
    public void ReleasePlatform_Current_IsTheRunningSystem()
    {
        Assert.Equal(OperatingSystem.IsMacOS() ? ReleasePlatform.MacOSArm64 : ReleasePlatform.WindowsX64, ReleasePlatform.Current);
        Assert.Equal("IlluxImageTool-Setup-0.2.0.exe", ReleaseClient.InstallerFileName(ReleasePlatform.WindowsX64, new Version(0, 2, 0)));
        Assert.Equal("IlluxImageTool-0.2.0-macOS-arm64.pkg", ReleaseClient.InstallerFileName(ReleasePlatform.MacOSArm64, new Version(0, 2, 0)));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta")]
    [InlineData("release-7")]
    public async Task Latest_UnparsableTag_Null(string tag)
    {
        var client = WindowsClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(tag)))));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_DraftOrPrerelease_Null()
    {
        Assert.Null(await WindowsClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(draft: true))))).GetLatestAsync(CancellationToken.None));
        Assert.Null(await WindowsClient(new HttpClient(new FakeHandler(_ => Text(ReleaseJson(prerelease: true))))).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_NoReleaseYet_Null_AndServerError_Throws()
    {
        var none = WindowsClient(new HttpClient(new FakeHandler(_ => Text("{}", HttpStatusCode.NotFound))));
        var broken = WindowsClient(new HttpClient(new FakeHandler(_ => Text("", HttpStatusCode.InternalServerError))));

        Assert.Null(await none.GetLatestAsync(CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => broken.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Latest_NonHttpsAsset_Null()
    {
        var json = ReleaseJson().Replace("https://github.com", "http://github.com");
        var client = WindowsClient(new HttpClient(new FakeHandler(_ => Text(json))));

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
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{Path.Combine(DownloadDir, "update-1.2.3.log")}\"",
            started.Args);
        var startInfo = Assert.Single(rig.StartInfos);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal(InstallerBytes, bytesSeenByInstaller);
        Assert.Equal("present", flagSeen); // flag is written before the installer starts
        Assert.True(File.Exists(AppPaths.UpdatingFlag)); // the installer (Task 14) removes it
        Assert.Equal(UpdatePhase.Installing, UpdateStateStore.Load().Phase);
        Assert.Equal([Path.Combine(DownloadDir, "IlluxImageTool-Setup-1.2.3.exe")], Directory.GetFiles(DownloadDir));
    }

    [Fact]
    public async Task Install_Mac_StartsInstallerWithPkgArgs()
    {
        Directory.CreateDirectory(DownloadDir);
        File.WriteAllText(Path.Combine(DownloadDir, "IlluxImageTool-0.9.0-macOS-arm64.pkg"), "old");
        File.WriteAllText(Path.Combine(DownloadDir, "IlluxImageTool-1.0.0-macOS-arm64.pkg.part"), "partial");
        File.WriteAllText(Path.Combine(DownloadDir, "notes.txt"), "keep");
        var rig = NewRig(r => Route(r, InstallerHash + "  " + MacPkg + "\n"), platform: ReleasePlatform.MacOSArm64);
        var pkg = Path.Combine(DownloadDir, MacPkg);
        byte[]? bytesSeenByInstaller = null;
        string? flagSeen = null;
        rig.OnStart = _ =>
        {
            bytesSeenByInstaller = File.ReadAllBytes(pkg);
            flagSeen = File.Exists(AppPaths.UpdatingFlag) ? "present" : "missing";
            return new Process();
        };

        var outcome = await rig.Updater.InstallAsync(MacRelease(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Started, outcome);
        Assert.Contains(rig.Handler.Requests, u => u == $"{InstallerHost}{MacPkg}");
        Assert.Contains(rig.Handler.Requests, u => u == $"{InstallerHost}{MacPkg}.sha256");
        var start = Assert.Single(rig.StartInfos);
        // /usr/sbin/installer -pkg <path> -target /, its output going straight into a log in the root-only update folder
        // (no pipe to this daemon, which the package stops while it installs). When the installer fails (before the
        // package's scripts ran, I3) the flag is removed; the stale-flag check finds the installer by its path.
        Assert.Equal("/bin/sh", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal("", start.Arguments);
        Assert.Equal(
            ["-c", "/usr/sbin/installer -pkg \"$1\" -target / >\"$2\" 2>&1 || /bin/rm -f \"$3\"", "sh", pkg,
             Path.Combine(DownloadDir, "update-1.2.3.log"), AppPaths.UpdatingFlag],
            start.ArgumentList);
        Assert.Equal(InstallerBytes, bytesSeenByInstaller);
        Assert.Equal("present", flagSeen);
        Assert.Equal(UpdatePhase.Installing, UpdateStateStore.Load().Phase);
        Assert.Equal(
            [MacPkg, "notes.txt"],
            Directory.GetFiles(DownloadDir).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public async Task Install_Mac_ChecksumMismatch_NotStarted_FileDeleted()
    {
        var rig = NewRig(r => Route(r, new string('0', 64)), platform: ReleasePlatform.MacOSArm64);

        Assert.Equal(InstallOutcome.ChecksumFailed, await rig.Updater.InstallAsync(MacRelease(), CancellationToken.None));

        Assert.Empty(rig.Started);
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
    }

    [Theory]
    [InlineData(ReleasePlatform.WindowsX64)]
    [InlineData(ReleasePlatform.MacOSArm64)]
    public async Task Install_SecondRequestWhileInstalling_TouchesNothing(ReleasePlatform platform)
    {
        // Any local user may send "install" (the macOS socket is 0666). While the started installer runs (updating.flag
        // present) a second request must not delete or replace the package it reads, nor start a second installer.
        var rig = NewRig(r => Route(r, InstallerHash), platform: platform);
        var release = platform == ReleasePlatform.MacOSArm64 ? MacRelease() : Release();
        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(release, CancellationToken.None));
        var installer = Assert.Single(Directory.GetFiles(DownloadDir));
        File.WriteAllText(Path.Combine(DownloadDir, "update-1.2.3.log"), "the running installer's log");
        var requests = rig.Handler.Requests.Count;
        var log = new ListLogger();
        var second = NewRig(r => Route(r, InstallerHash), platform: platform, log: log);

        var outcome = await second.Updater.InstallAsync(release, CancellationToken.None);

        Assert.Equal(InstallOutcome.AlreadyInstalling, outcome);
        Assert.Empty(second.Handler.Requests);
        Assert.Empty(second.Started);
        Assert.Equal(requests, rig.Handler.Requests.Count);
        Assert.Equal(InstallerBytes, File.ReadAllBytes(installer));
        Assert.Equal("the running installer's log", File.ReadAllText(Path.Combine(DownloadDir, "update-1.2.3.log")));
        Assert.Equal(2, Directory.GetFiles(DownloadDir).Length);
        Assert.True(File.Exists(AppPaths.UpdatingFlag));
        Assert.Equal(UpdatePhase.Installing, UpdateStateStore.Load().Phase);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("already being installed"));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public async Task Install_Mac_InstallerCommand_WritesItsOutputIntoTheUpdateFolderLog()
    {
        // The real command, with /usr/sbin/installer swapped for a script that prints its arguments, in a folder whose
        // name has spaces like "/Library/Application Support/Illux Image Tool/update".
        var update = Path.Combine(Dir, "Application Support", "update");
        var fake = Path.Combine(Dir, "fake installer.sh");
        Directory.CreateDirectory(Dir);
        File.WriteAllText(fake, "#!/bin/sh\nprintf '%s\\n' \"$@\"\necho to-stderr >&2\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var rig = NewRig(r => Route(r, InstallerHash), platform: ReleasePlatform.MacOSArm64, downloadDirectory: update);
        Process? ran = null;
        rig.OnStart = _ =>
        {
            var real = rig.StartInfos[^1];
            var swapped = new ProcessStartInfo(real.FileName) { UseShellExecute = false };
            foreach (var argument in real.ArgumentList) swapped.ArgumentList.Add(argument.Replace("/usr/sbin/installer", $"\"{fake}\""));
            ran = Process.Start(swapped);
            return ran is null ? null : new Process(); // the updater disposes what it gets: keep the real one for waiting
        };

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(MacRelease(), CancellationToken.None));

        Assert.NotNull(ran);
        Assert.True(ran.WaitForExit(10000));
        Assert.Equal(0, ran.ExitCode);
        Assert.Equal(
            ["-pkg", Path.Combine(update, MacPkg), "-target", "/", "to-stderr"],
            File.ReadAllLines(Path.Combine(update, "update-1.2.3.log")));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public async Task Install_Mac_InstallerThatFails_RemovesTheFlag_OneThatWorks_DoesNot()
    {
        // I3: a package the installer rejects (corrupt, or refused before preinstall) must not leave updating.flag.
        foreach (var (exitCode, flagLeft) in new[] { (1, false), (0, true) })
        {
            if (File.Exists(AppPaths.UpdatingFlag)) File.Delete(AppPaths.UpdatingFlag);
            var update = Path.Combine(Dir, $"update-{exitCode}");
            var fake = Path.Combine(Dir, $"fake installer {exitCode}.sh");
            Directory.CreateDirectory(Dir);
            File.WriteAllText(fake, $"#!/bin/sh\nexit {exitCode}\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var rig = NewRig(r => Route(r, InstallerHash), platform: ReleasePlatform.MacOSArm64, downloadDirectory: update);
            Process? ran = null;
            rig.OnStart = _ =>
            {
                var real = rig.StartInfos[^1];
                var swapped = new ProcessStartInfo(real.FileName) { UseShellExecute = false };
                foreach (var argument in real.ArgumentList) swapped.ArgumentList.Add(argument.Replace("/usr/sbin/installer", $"\"{fake}\""));
                ran = Process.Start(swapped);
                return ran is null ? null : new Process();
            };

            Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(MacRelease(), CancellationToken.None));

            Assert.NotNull(ran);
            Assert.True(ran.WaitForExit(10000));
            Assert.Equal(flagLeft, File.Exists(AppPaths.UpdatingFlag));
        }
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
        var rig = NewRig(r => Route(r, "\uFEFF" + InstallerHash + " *IlluxImageTool-Setup-1.2.3.exe\r\n"));

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(Release(), CancellationToken.None));
    }

    [Fact]
    public async Task Install_ActiveRunLock_WaitsThenStarts()
    {
        var held = RunLock.Acquire();
        var phaseWhileWaiting = "";
        var waits = 0;
        // Deterministic: the run "finishes" exactly when the updater goes to sleep for its re-check.
        var time = new HookedTimeProvider(() =>
        {
            waits++;
            phaseWhileWaiting = UpdateStateStore.Load().Phase;
            held.Dispose();
        });
        var rig = NewRig(r => Route(r, InstallerHash), time: time);
        rig.OnStart = _ =>
        {
            Assert.False(RunLock.AnyActive()); // the installer must not start while a run lock is held
            return new Process();
        };

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Started, outcome);
        Assert.Equal(1, waits);
        Assert.Equal(UpdatePhase.WaitingForJobs, phaseWhileWaiting);
        Assert.Single(rig.Started);
    }

    /// <summary>Runs a hook whenever something waits on a timer, then lets the wait finish immediately.</summary>
    private sealed class HookedTimeProvider(Action onWait) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            onWait();
            return base.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    [Fact]
    public async Task Install_InstallerChangedDuringIdleWait_ChecksumFailed_NotStarted()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        var polls = 0;
        rig.Busy = () =>
        {
            if (++polls == 1)
                File.AppendAllText(Path.Combine(DownloadDir, "IlluxImageTool-Setup-1.2.3.exe"), "tampered"); // verified file swapped
            return false;
        };

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.ChecksumFailed, outcome);
        Assert.Empty(rig.Started);
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.Equal(UpdatePhase.Failed, UpdateStateStore.Load().Phase);
    }

    [WindowsFact] // macOS has advisory locks only; there the root-only 0700 update folder keeps others out
    public async Task Install_InstallerLockedAgainstWriteAndDeleteWhileStarting()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        var path = Path.Combine(DownloadDir, "IlluxImageTool-Setup-1.2.3.exe");
        var writeBlocked = false;
        var deleteBlocked = false;
        rig.OnStart = _ =>
        {
            try { File.AppendAllText(path, "x"); } catch (IOException) { writeBlocked = true; }
            try { File.Delete(path); } catch (IOException) { deleteBlocked = true; }
            return new Process();
        };

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(Release(), CancellationToken.None));

        Assert.True(writeBlocked);
        Assert.True(deleteBlocked);
        Assert.Equal(InstallerBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Install_JobStartsAfterFlagWritten_FlagRemovedAndWaitsAgain()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        var polls = 0;
        rig.Busy = () =>
        {
            polls++;
            if (polls == 2)
            {
                Assert.True(File.Exists(AppPaths.UpdatingFlag)); // the re-check happens with the flag in place
                return true;
            }
            if (polls == 3) Assert.False(File.Exists(AppPaths.UpdatingFlag)); // removed while waiting again
            return false;
        };
        rig.OnStart = _ =>
        {
            Assert.True(File.Exists(AppPaths.UpdatingFlag));
            return new Process();
        };

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(Release(), CancellationToken.None));
        Assert.Equal(4, polls);
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
            Assert.Equal(5, polls); // idle is re-checked once more after the flag is written
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
            phases.Distinct()); // Distinct keeps first-seen order; the idle check runs twice (before and after the flag)
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

    [Theory]
    [InlineData("https://github.com/Illux889/illux-image-tool/releases/download/v1.2.3/x.exe", true)]
    [InlineData("https://objects.githubusercontent.com/some/path/x.exe", true)]
    [InlineData("https://release-assets.githubusercontent.com/x.exe", true)]
    [InlineData("https://evil.example.com/x.exe", false)]
    [InlineData("https://github.com.evil.example.com/x.exe", false)]
    [InlineData("https://evilgithubusercontent.com/x.exe", false)]
    [InlineData("https://github.com@evil.example.com/x.exe", false)]
    [InlineData("https://user:pw@github.com/x.exe", false)]
    [InlineData("http://github.com/x.exe", false)]
    public void ReleaseClient_OnlyGitHubHttpsHostsAreTrusted(string url, bool trusted)
    {
        Assert.Equal(trusted, ReleaseClient.IsTrustedDownload(new Uri(url)));
    }

    [Fact]
    public async Task Latest_AssetOnForeignHost_Null()
    {
        var json = ReleaseJson().Replace("https://github.com/", "https://evil.example.com/");
        var client = WindowsClient(new HttpClient(new FakeHandler(_ => Text(json))));

        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Install_OversizedChecksumFile_Failed_InstallerNeverRequested()
    {
        var rig = NewRig(r => Route(r, InstallerHash + new string(' ', 5000)));

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.DoesNotContain(rig.Handler.Requests, u => u.EndsWith(".exe", StringComparison.Ordinal));
        Assert.Empty(rig.Started);
        Assert.Equal(UpdatePhase.Failed, UpdateStateStore.Load().Phase);
    }

    [Fact]
    public async Task Install_InstallerLargerThanLimit_Failed_NoFileLeftBehind()
    {
        // Content-Length says it is too big.
        var rig = NewRig(r => Route(r, InstallerHash), maxInstallerBytes: InstallerBytes.Length - 1);
        Assert.Equal(InstallOutcome.Failed, await rig.Updater.InstallAsync(Release(), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(DownloadDir));

        // No Content-Length: the limit is enforced while streaming.
        var chunked = NewRig(
            r => IsInstallerRequest(r)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(InstallerBytes)) { Headers = { ContentLength = null } } }
                : Route(r, InstallerHash),
            maxInstallerBytes: 8);
        Assert.Equal(InstallOutcome.Failed, await chunked.Updater.InstallAsync(Release(), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(DownloadDir));
        Assert.Empty(chunked.Started);
    }

    [Fact]
    public async Task Install_RemovesOlderInstallersAndPartials_KeepsOtherFiles()
    {
        Directory.CreateDirectory(DownloadDir);
        File.WriteAllText(Path.Combine(DownloadDir, "IlluxImageTool-Setup-0.9.0.exe"), "old");
        File.WriteAllText(Path.Combine(DownloadDir, "IlluxImageTool-Setup-1.0.0.exe.part"), "partial");
        File.WriteAllText(Path.Combine(DownloadDir, "notes.txt"), "keep");
        var rig = NewRig(r => Route(r, InstallerHash));

        Assert.Equal(InstallOutcome.Started, await rig.Updater.InstallAsync(Release(), CancellationToken.None));

        Assert.Equal(
            ["IlluxImageTool-Setup-1.2.3.exe", "notes.txt"],
            Directory.GetFiles(DownloadDir).Select(f => Path.GetFileName(f)!).Order().ToArray());
    }

    [Fact]
    public async Task Install_DefaultDownloadDirectory_IsUnderAppRoot()
    {
        var handler = new FakeHandler(r => Route(r, InstallerHash));
        var http = new HttpClient(handler);
        string? startedPath = null;
        var updater = new Updater(
            WindowsClient(http), http, new Version(1, 0, 0), () => false,
            start => { startedPath = start.FileName; return new Process(); },
            NullLogger.Instance, trustDownloadDirectory: true);

        await updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(Path.Combine(AppRoot, "update", "IlluxImageTool-Setup-1.2.3.exe"), startedPath);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void DownloadDirectory_OnlySystemAndAdministratorsAreTrustedOwners()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        System.Security.Principal.SecurityIdentifier Sid(System.Security.Principal.WellKnownSidType t) => new(t, null);

        Assert.True(DownloadDirectory.IsTrustedOwner(Sid(System.Security.Principal.WellKnownSidType.LocalSystemSid)));
        Assert.True(DownloadDirectory.IsTrustedOwner(Sid(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)));
        Assert.False(DownloadDirectory.IsTrustedOwner(Sid(System.Security.Principal.WellKnownSidType.WorldSid)));
        Assert.False(DownloadDirectory.IsTrustedOwner(Sid(System.Security.Principal.WellKnownSidType.BuiltinUsersSid)));
        Assert.False(DownloadDirectory.IsTrustedOwner(Sid(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid)));
        Assert.False(DownloadDirectory.IsTrustedOwner(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001")));
    }

    [FactWhenJunctionsWork]
    public void DownloadDirectory_Junction_IsRefused()
    {
        var target = Path.Combine(Dir, "target");
        var link = Path.Combine(Dir, "link");
        Directory.CreateDirectory(target);
        Assert.True(TryCreateJunction(link, target));

        Assert.Contains("link", DownloadDirectory.Verify(link));
    }

    [FactWhenJunctionsWork]
    public void DownloadDirectory_BelowJunction_IsRefused()
    {
        var target = Path.Combine(Dir, "target");
        var junction = Path.Combine(Dir, "junction");
        Directory.CreateDirectory(Path.Combine(target, "update"));
        Assert.True(TryCreateJunction(junction, target));

        var problem = DownloadDirectory.Verify(Path.Combine(junction, "update"));

        Assert.NotNull(problem);
        Assert.Contains("link", problem);
    }

    private static bool TryCreateJunction(string link, string target) => FactWhenJunctionsWorkAttribute.TryCreateJunction(link, target);

    [FactWhenNotElevated]
    public void DownloadDirectory_FolderOwnedByOrdinaryUser_IsRefused()
    {
        var folder = Path.Combine(Dir, "mine");
        Directory.CreateDirectory(folder);

        var problem = DownloadDirectory.Verify(folder);

        Assert.NotNull(problem);
        Assert.Contains("not owned by SYSTEM or Administrators", problem);
    }

    [FactWhenNotElevated]
    public async Task Install_DefaultSecurity_UserOwnedFolder_Refused_NothingDownloaded()
    {
        Directory.CreateDirectory(DownloadDir); // pre-created by an ordinary user, as an attacker would
        var rig = NewRig(r => Route(r, InstallerHash), trust: false);

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Started);
        var state = UpdateStateStore.Load();
        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.Contains("not owned", state.Error);
    }

    [FactWhenJunctionsWork]
    public async Task Install_DefaultSecurity_JunctionFolder_Refused_NothingDownloadedOrDeleted()
    {
        // The junction points at a folder that holds files with exactly the names the updater would clean up.
        var target = Path.Combine(Dir, "elsewhere");
        Directory.CreateDirectory(target);
        var victim = Path.Combine(target, "IlluxImageTool-Setup-1.2.3.exe");
        var victimPart = victim + ".part";
        File.WriteAllText(victim, "someone else's file");
        File.WriteAllText(victimPart, "someone else's partial file");
        Assert.True(TryCreateJunction(DownloadDir, target));
        var rig = NewRig(r => Route(r, InstallerHash), trust: false);

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Started);
        Assert.Equal("someone else's file", File.ReadAllText(victim));
        Assert.Equal("someone else's partial file", File.ReadAllText(victimPart));
        Assert.Equal(UpdatePhase.Failed, UpdateStateStore.Load().Phase);
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private string MacUpdateDir => Path.Combine(AppRoot, "update");

    /// <summary>Tests run as an ordinary user: the owner the daemon insists on (root in production) is this user here.</summary>
    [SupportedOSPlatform("macos")]
    private static uint Me => UnixNative.EffectiveUserId;

    [SupportedOSPlatform("macos")]
    private static string? PrepareAsMe(string directory) => DownloadDirectory.PrepareTrusted(directory, Me);

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_FolderOfAnotherOwner_IsRefused_NothingCreated()
    {
        Directory.CreateDirectory(AppRoot);

        var problem = DownloadDirectory.PrepareTrusted(MacUpdateDir, Me + 1);

        Assert.NotNull(problem);
        Assert.Contains($"owned by user {Me}, not {Me + 1}", problem);
        Assert.False(Directory.Exists(MacUpdateDir));

        Directory.CreateDirectory(MacUpdateDir, OwnerOnly);
        Assert.Contains("owned by user", DownloadDirectory.Verify(MacUpdateDir, Me + 1));
        Assert.Null(DownloadDirectory.Verify(MacUpdateDir, Me));
    }

    [MacFactWhenNotRoot] // the refusal needs a folder that root does not own
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_ProductionOwnerIsRoot()
    {
        Assert.Equal(0u, DownloadDirectory.RootUserId);
        Directory.CreateDirectory(AppRoot);

        var problem = DownloadDirectory.PrepareTrusted(MacUpdateDir);

        Assert.NotNull(problem);
        Assert.Contains("not 0", problem);
        Assert.False(Directory.Exists(MacUpdateDir));
    }

    [MacFactWhenNotRoot]
    [SupportedOSPlatform("macos")]
    public async Task Install_MacPkg_DefaultSecurity_FolderNotOwnedByRoot_Refused_NothingDownloaded()
    {
        Directory.CreateDirectory(AppRoot);
        var rig = NewRig(r => Route(r, InstallerHash), trust: false, downloadDirectory: MacUpdateDir, platform: ReleasePlatform.MacOSArm64);

        var outcome = await rig.Updater.InstallAsync(MacRelease(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Started);
        Assert.Contains("not 0", UpdateStateStore.Load().Error);
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_CreatedBelowRoot_OwnerOnly()
    {
        Directory.CreateDirectory(AppRoot);

        Assert.Null(PrepareAsMe(MacUpdateDir));

        Assert.True(Directory.Exists(MacUpdateDir));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(MacUpdateDir));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_ExistingFolderIsTightened()
    {
        Directory.CreateDirectory(MacUpdateDir);
        File.SetUnixFileMode(MacUpdateDir, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Assert.Null(PrepareAsMe(MacUpdateDir));

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(MacUpdateDir));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_FolderIsALink_IsRefused()
    {
        var target = Directory.CreateDirectory(Path.Combine(Dir, "elsewhere")).FullName;
        var targetMode = File.GetUnixFileMode(target);
        Directory.CreateDirectory(AppRoot);
        Directory.CreateSymbolicLink(MacUpdateDir, target);

        var problem = PrepareAsMe(MacUpdateDir);

        Assert.NotNull(problem);
        Assert.Contains("link", problem);
        Assert.Equal(targetMode, File.GetUnixFileMode(target)); // the link's target is not re-permissioned
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_RootIsALink_IsRefused()
    {
        var target = Directory.CreateDirectory(Path.Combine(Dir, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(AppRoot, target);

        var problem = PrepareAsMe(MacUpdateDir);

        Assert.NotNull(problem);
        Assert.Contains("link", problem);
        Assert.False(Directory.Exists(Path.Combine(target, "update")));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_BelowALinkBelowRoot_IsRefused()
    {
        var target = Directory.CreateDirectory(Path.Combine(Dir, "elsewhere")).FullName;
        Directory.CreateDirectory(AppRoot);
        Directory.CreateSymbolicLink(Path.Combine(AppRoot, "sub"), target);

        var problem = PrepareAsMe(Path.Combine(AppRoot, "sub", "update"));

        Assert.NotNull(problem);
        Assert.Contains("link", problem);
        Assert.False(Directory.Exists(Path.Combine(target, "update")));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_GroupOrOtherWritable_IsRefused()
    {
        Directory.CreateDirectory(MacUpdateDir);
        File.SetUnixFileMode(AppRoot, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        Assert.Contains("writable", PrepareAsMe(MacUpdateDir));

        File.SetUnixFileMode(AppRoot, OwnerOnly);
        File.SetUnixFileMode(MacUpdateDir, OwnerOnly | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        Assert.Contains("writable", PrepareAsMe(MacUpdateDir));
        Assert.Equal(OwnerOnly | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute, File.GetUnixFileMode(MacUpdateDir)); // refused, not repaired
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public void DownloadDirectory_Mac_OutsideRoot_IsRefused()
    {
        var problem = PrepareAsMe(DownloadDir);

        Assert.NotNull(problem);
        Assert.Contains("not below", problem);
        Assert.False(Directory.Exists(DownloadDir));
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public async Task Install_Mac_DefaultSecurity_LinkedFolder_Refused_NothingDownloadedOrDeleted()
    {
        var target = Path.Combine(Dir, "elsewhere");
        Directory.CreateDirectory(target);
        var victim = Path.Combine(target, "IlluxImageTool-Setup-1.2.3.exe");
        File.WriteAllText(victim, "someone else's file");
        Directory.CreateDirectory(AppRoot);
        Directory.CreateSymbolicLink(MacUpdateDir, target);
        var rig = NewRig(r => Route(r, InstallerHash), trust: false, downloadDirectory: MacUpdateDir, downloadDirectoryOwner: Me);

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Empty(rig.Handler.Requests);
        Assert.Empty(rig.Started);
        Assert.Equal("someone else's file", File.ReadAllText(victim));
        Assert.Contains("link", UpdateStateStore.Load().Error);
    }

    [MacFact]
    [SupportedOSPlatform("macos")]
    public async Task Install_Mac_DefaultSecurity_SafeFolder_Starts()
    {
        Directory.CreateDirectory(AppRoot);
        var rig = NewRig(r => Route(r, InstallerHash), trust: false, downloadDirectory: MacUpdateDir, downloadDirectoryOwner: Me);

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Started, outcome);
        Assert.Single(rig.Started);
    }

    [Fact]
    public async Task Install_StartThrowsCancellation_FlagStillRemoved()
    {
        var rig = NewRig(r => Route(r, InstallerHash));
        rig.OnStart = _ => throw new OperationCanceledException("not ours");

        var outcome = await rig.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public void StateStore_TryLoad_DistinguishesMissingAndCorruptFromUnreadable()
    {
        Assert.True(UpdateStateStore.TryLoad(out var missing));
        Assert.Equal(UpdatePhase.Idle, missing.Phase);

        UpdateStateStore.Save(new UpdateState { AvailableVersion = "1.1.0" });
        using (new FileStream(UpdateStateStore.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(UpdateStateStore.TryLoad(out _));
        }

        File.WriteAllText(UpdateStateStore.FilePath, "{ not json");
        Assert.True(UpdateStateStore.TryLoad(out var corrupt));
        Assert.Equal(UpdatePhase.Idle, corrupt.Phase);
    }

    [Fact]
    public async Task Check_StateFileUnreadable_DoesNotOverwriteSavedState()
    {
        var rig = NewRig(r => Route(r, InstallerHash), current: "1.0.0");
        UpdateStateStore.Save(new UpdateState { AvailableVersion = "9.9.9", Phase = UpdatePhase.Installing });

        using (new FileStream(UpdateStateStore.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await rig.Updater.CheckAsync(CancellationToken.None);
        }

        var state = UpdateStateStore.Load();
        Assert.Equal("9.9.9", state.AvailableVersion);
        Assert.Equal(UpdatePhase.Installing, state.Phase);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Check_StateNotWritable_OneClearError()
    {
        // What a named service account that is not a local Administrator meets: Root is SYSTEM/Administrators only.
        Directory.CreateDirectory(AppPaths.Root);
        var root = new DirectoryInfo(AppPaths.Root);
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me.User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Deny);
        var security = root.GetAccessControl();
        security.AddAccessRule(deny);
        root.SetAccessControl(security);
        try
        {
            var log = new ListLogger();
            var rig = NewRig(r => Route(r, InstallerHash), log: log);

            await rig.Updater.CheckAsync(CancellationToken.None);
            await rig.Updater.CheckAsync(CancellationToken.None);

            var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
            Assert.Contains("may not write update-state.json", error.Message);
            Assert.Contains("local Administrator", error.Message);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            root.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task Check_LogsEveryResult()
    {
        var log = new ListLogger();

        await NewRig(r => Route(r, InstallerHash), current: "1.0.0", log: log).Updater.CheckAsync(CancellationToken.None);
        await NewRig(r => Route(r, InstallerHash), current: "1.2.3", log: log).Updater.CheckAsync(CancellationToken.None);
        await NewRig(_ => Text("{}", HttpStatusCode.NotFound), log: log).Updater.CheckAsync(CancellationToken.None);
        await NewRig(_ => throw new HttpRequestException("offline"), log: log).Updater.CheckAsync(CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("1.2.3 is available"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("up to date"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("no usable release"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("check failed"));
    }

    [Fact]
    public async Task Install_LogsOutcomes()
    {
        var log = new ListLogger();

        await NewRig(r => Route(r, InstallerHash), log: log).Updater.InstallAsync(Release(), CancellationToken.None);
        File.Delete(AppPaths.UpdatingFlag); // as the installer does when it is done
        await NewRig(r => Route(r, new string('0', 64)), log: log).Updater.InstallAsync(Release(), CancellationToken.None);
        var busy = NewRig(r => Route(r, InstallerHash), giveUp: TimeSpan.FromMilliseconds(30), log: log);
        busy.Busy = () => true;
        await busy.Updater.InstallAsync(Release(), CancellationToken.None);
        var nullStart = NewRig(r => Route(r, InstallerHash), log: log);
        nullStart.OnStart = _ => null;
        await nullStart.Updater.InstallAsync(Release(), CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("installer started"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("ChecksumFailed"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Busy"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be started"));
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
