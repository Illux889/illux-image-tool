using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Illux.ImageTool.Tests;

/// <summary>
/// The macOS daemon's control socket. Tests run as an ordinary user, so the owner the daemon insists on (root in
/// production) is this user's id here. The root is a short folder in /tmp: a Unix socket path may have at most 103
/// bytes, which the per-user temp folder of macOS can exceed.
/// </summary>
[Collection("AppRoot")]
[SupportedOSPlatform("macos")]
public sealed class UnixSocketSignalsTests : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(AppPaths.RootOverrideVariable);
    private readonly string _root = Path.Combine("/tmp", "illux-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _outside = Path.Combine("/tmp", "illux-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly List<IDisposable> _cleanup = [];

    public UnixSocketSignalsTests()
    {
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, _root);
        if (OperatingSystem.IsMacOS()) Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var item in _cleanup) item.Dispose();
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, _previousRoot);
        foreach (var dir in new[] { _root, _outside })
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static uint Me => UnixNative.EffectiveUserId;

    private string RunDir => Path.Combine(_root, "run");

    private string SocketPath => Path.Combine(_root, "run", "control.sock");

    private UnixSocketSignals Start(uint? owner = null)
    {
        var signals = UnixSocketSignals.Start(_root, owner ?? Me, NullLogger.Instance);
        _cleanup.Add(signals);
        return signals;
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Sends raw bytes the way any local process could, then closes.</summary>
    private void SendRaw(byte[] bytes)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(SocketPath));
        client.Send(bytes);
        // Closing ends the request; no shutdown, which fails when the daemon already closed its end.
    }

    private void SendRaw(string text) => SendRaw(Encoding.UTF8.GetBytes(text));

    [MacFact]
    public void Paths_AreRunControlSockBelowRoot()
    {
        Assert.Equal(Path.Combine(AppPaths.MacRoot, "run", "control.sock"), UnixSocketSignals.SocketPath(AppPaths.MacRoot));
        Assert.Equal("/Library/Application Support/Illux Image Tool/run/control.sock", UnixSocketSignals.SocketPath(AppPaths.MacRoot));
        Assert.Equal(1024, UnixSocketSignals.MaxRequestBytes);
        Assert.Equal(TimeSpan.FromSeconds(2), UnixSocketSignals.RequestTimeout);
    }

    [MacFact]
    public void Socket_CheckAndInstall_Signal()
    {
        using var signals = Start();
        Assert.False(signals.TryWaitCheckNow(TimeSpan.Zero));
        Assert.False(signals.TryWaitInstallNow(TimeSpan.Zero));

        Assert.True(ServiceRequests.RequestCheckNow(out var error), error);
        Assert.True(signals.TryWaitCheckNow(Wait));
        Assert.False(signals.TryWaitInstallNow(TimeSpan.Zero));

        Assert.True(ServiceRequests.RequestInstallNow());
        Assert.True(signals.TryWaitInstallNow(Wait));
        Assert.False(signals.TryWaitCheckNow(TimeSpan.Zero));
    }

    [MacFact]
    public void Socket_TwoRequestsBeforeTheDaemonLooked_AreOneRequest()
    {
        using var signals = Start();

        SendRaw("check\n");
        SendRaw("check\n");
        SendRaw("install\n"); // handled after both checks: one connection at a time
        Assert.True(signals.TryWaitInstallNow(Wait));

        Assert.True(signals.TryWaitCheckNow(TimeSpan.Zero));
        Assert.False(signals.TryWaitCheckNow(TimeSpan.Zero));
    }

    [MacFact]
    public void Socket_CommandIsTrimmed_AndMayEndWithoutNewline()
    {
        using var signals = Start();

        SendRaw("  check \r\n");
        Assert.True(signals.TryWaitCheckNow(Wait));
        SendRaw("install");
        Assert.True(signals.TryWaitInstallNow(Wait));
    }

    [MacFact]
    public void Socket_GarbageIgnored()
    {
        using var signals = Start();

        SendRaw("CHECK\n");
        SendRaw("Install\n");
        SendRaw("check install\n");
        SendRaw("checkinstall\n");
        SendRaw("hello\n");
        SendRaw("");
        SendRaw("\n");
        SendRaw("check\0\n");
        SendRaw(new byte[] { 0xff, 0xfe, 0x00, 0x63 });
        SendRaw(new string('x', 2000) + "\ncheck\n"); // more than 1 KB before the line end
        SendRaw(Encoding.ASCII.GetBytes(new string(' ', 1100) + "check\n"));
        SendRaw("install\n"); // the marker: everything above was handled before it

        Assert.True(signals.TryWaitInstallNow(Wait));
        Assert.False(signals.TryWaitCheckNow(TimeSpan.Zero));
        Assert.False(signals.TryWaitInstallNow(TimeSpan.Zero));
    }

    [MacFact]
    public void Socket_OnlyTheFirstLineCounts()
    {
        using var signals = Start();

        SendRaw("hello\ncheck\n");
        SendRaw("install\ncheck\n");

        Assert.True(signals.TryWaitInstallNow(Wait));
        Assert.False(signals.TryWaitCheckNow(TimeSpan.Zero));
    }

    [MacFact]
    public void Socket_SlowClientIsDroppedAfterTwoSeconds_AndDoesNotBlockLaterRequests()
    {
        using var signals = Start();
        using var slow = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        slow.ReceiveTimeout = 5000; // set up front: macOS refuses socket options once the peer has closed
        slow.Connect(new UnixDomainSocketEndPoint(SocketPath));
        slow.Send("che"u8.ToArray()); // and never finishes the line
        var watch = Stopwatch.StartNew();

        SendRaw("install\n");
        Assert.True(signals.TryWaitInstallNow(Wait), "a request behind a slow client must still arrive");
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(8));

        Assert.Equal(0, slow.Receive(new byte[16])); // the daemon closed it
        Assert.False(signals.TryWaitCheckNow(TimeSpan.Zero));
    }

    [MacFact]
    public void Socket_ManyIdleClients_DaemonStillStopsPromptly()
    {
        var signals = Start();
        var idle = new List<Socket>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                idle.Add(s);
                s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            }

            var watch = Stopwatch.StartNew();
            signals.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stopping took {watch.Elapsed}");
        }
        finally
        {
            foreach (var s in idle) s.Dispose();
        }
    }

    [MacFact]
    public void Socket_RunFolderIs0755_SocketIs0666_BothOwnedByTheDaemon()
    {
        using var signals = Start();

        var run = UnixNative.LStat(RunDir)!.Value;
        Assert.Equal(UnixNative.DirectoryType, run.Type);
        Assert.Equal(Me, run.Owner);
        Assert.Equal((UnixFileMode)0b111_101_101, run.Mode);

        var socket = UnixNative.LStat(SocketPath)!.Value;
        Assert.Equal(UnixNative.SocketType, socket.Type);
        Assert.Equal(Me, socket.Owner);
        Assert.Equal((UnixFileMode)0b110_110_110, socket.Mode);
    }

    [MacFact]
    public void Socket_ExistingRunFolderWithOtherMode_IsSetTo0755()
    {
        Directory.CreateDirectory(RunDir);
        File.SetUnixFileMode(RunDir, (UnixFileMode)0b111_111_111);

        using var signals = Start();

        Assert.Equal((UnixFileMode)0b111_101_101, UnixNative.LStat(RunDir)!.Value.Mode);
    }

    [MacFact]
    public void Socket_RunFolderOwnedBySomeoneElse_IsRefused()
    {
        Directory.CreateDirectory(RunDir);

        Assert.Throws<UnauthorizedAccessException>(() => Start(owner: Me + 1));
        Assert.False(File.Exists(SocketPath));
    }

    [MacFact]
    public void Socket_NewRunFolderNotOwnedByTheExpectedUser_IsRefused()
    {
        // In production the daemon runs as root and expects root: a folder this process creates must have that owner.
        Assert.Throws<UnauthorizedAccessException>(() => Start(owner: 0 == Me ? 1u : 0u));
        Assert.False(File.Exists(SocketPath));
    }

    [MacFact]
    public void Socket_RunFolderIsALink_IsRefused()
    {
        Directory.CreateDirectory(_outside);
        Directory.CreateSymbolicLink(RunDir, _outside);

        Assert.Throws<IOException>(() => Start());
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [MacFact]
    public void Socket_RootIsALink_IsRefused()
    {
        Directory.Delete(_root);
        Directory.CreateDirectory(_outside);
        Directory.CreateSymbolicLink(_root, _outside);

        Assert.Throws<IOException>(() => Start());
        Assert.Empty(Directory.GetFileSystemEntries(_outside));
    }

    [MacFact]
    public void Socket_RunIsAFile_IsRefused()
    {
        File.WriteAllText(RunDir, "not a folder");

        Assert.Throws<IOException>(() => Start());
        Assert.Equal("not a folder", File.ReadAllText(RunDir));
    }

    [MacFact]
    public void Socket_StaleSocketReplaced()
    {
        // A socket file nobody listens on any more (the daemon was killed): moved away from the path the socket was
        // bound to, so disposing it cannot remove it.
        Directory.CreateDirectory(RunDir);
        var bound = Path.Combine(RunDir, "old.sock");
        using (var old = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            old.Bind(new UnixDomainSocketEndPoint(bound));
            old.Listen(1);
            File.Move(bound, SocketPath);
        }
        Assert.Equal(UnixNative.SocketType, UnixNative.LStat(SocketPath)!.Value.Type);
        Assert.False(ServiceRequests.RequestCheckNow());

        using var signals = Start();

        Assert.True(ServiceRequests.RequestCheckNow());
        Assert.True(signals.TryWaitCheckNow(Wait));
    }

    [MacFact]
    public void Socket_RegularFileAtSocketPath_IsLeftAloneAndRefused()
    {
        Directory.CreateDirectory(RunDir);
        File.WriteAllText(SocketPath, "precious");

        Assert.Throws<IOException>(() => Start());
        Assert.Equal("precious", File.ReadAllText(SocketPath));
    }

    [MacFact]
    public void Socket_LinkAtSocketPath_IsLeftAloneAndRefused()
    {
        Directory.CreateDirectory(RunDir);
        Directory.CreateDirectory(_outside);
        var target = Path.Combine(_outside, "target");
        File.WriteAllText(target, "precious");
        File.CreateSymbolicLink(SocketPath, target);

        Assert.Throws<IOException>(() => Start());
        Assert.NotNull(new FileInfo(SocketPath).LinkTarget);
        Assert.Equal("precious", File.ReadAllText(target));
    }

    [MacFact]
    public void Socket_FolderAtSocketPath_IsLeftAloneAndRefused()
    {
        Directory.CreateDirectory(SocketPath);

        Assert.Throws<IOException>(() => Start());
        Assert.True(Directory.Exists(SocketPath));
    }

    [MacFact]
    public void Socket_StaleSocketOfAnotherUser_IsRefused()
    {
        Directory.CreateDirectory(RunDir);
        var bound = Path.Combine(RunDir, "old.sock");
        using (var old = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            old.Bind(new UnixDomainSocketEndPoint(bound));
            File.Move(bound, SocketPath);
        }

        // The run folder passes (its owner is checked against the same id), so pretend the socket's owner differs by
        // expecting another owner for the socket only.
        Assert.Throws<UnauthorizedAccessException>(() => UnixSocketSignals.RemoveStaleSocket(SocketPath, Me + 1));
        Assert.Equal(UnixNative.SocketType, UnixNative.LStat(SocketPath)!.Value.Type);
    }

    [MacFact]
    public void Requests_WithoutDaemon_FailClearly()
    {
        Assert.False(ServiceRequests.RequestCheckNow(out var error));
        Assert.Contains("not running", error);
        Assert.False(ServiceRequests.RequestInstallNow(out error));
        Assert.Contains("not running", error);
    }

    [MacFact]
    public void Requests_AfterTheDaemonStopped_FailClearly()
    {
        Start().Dispose();

        Assert.False(ServiceRequests.RequestCheckNow(out var error));
        Assert.Contains("not running", error);
    }
}
