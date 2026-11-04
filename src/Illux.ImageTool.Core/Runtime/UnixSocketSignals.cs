using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Runtime;

/// <summary>
/// The macOS daemon's <see cref="IServiceSignals"/>: a Unix socket <c>run/control.sock</c> below
/// <see cref="AppPaths.Root"/> that any local user may connect to and send one line, <c>check</c> or <c>install</c>.
/// <list type="bullet">
/// <item>The daemon creates or verifies <c>run/</c>: a real folder (no link, and Root is no link either) owned by the
/// daemon's user (root in production), mode 0755, so nobody else can replace the socket.</item>
/// <item>A stale <c>control.sock</c> is removed only when it is a socket owned by the daemon's user; anything else there
/// (a file, folder, link, someone else's socket) is left alone and the socket is not opened.</item>
/// <item>The socket is made 0666 after binding. One connection is handled at a time, on its own task: at most
/// <see cref="MaxRequestBytes"/> are read, for at most <see cref="RequestTimeout"/>; the first line, trimmed, must be
/// exactly <c>check</c> or <c>install</c>, everything else is ignored. A slow client therefore delays other requests
/// by at most two seconds and never the daemon's other work.</item>
/// <item>A request is only a trigger (R37): nothing else is read from it, and the daemon does its own update check.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class UnixSocketSignals : IServiceSignals
{
    public const string CheckCommand = "check";
    public const string InstallCommand = "install";
    public const int MaxRequestBytes = 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The daemon runs as root; <see cref="Start(string, uint, ILogger)"/> takes another owner for tests.</summary>
    public const uint RootUserId = 0;

    private const UnixFileMode RunFolderMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    private const int Backlog = 8;

    private readonly Socket _listener;
    private readonly ILogger _log;
    private readonly AutoResetEvent _checkNow = new(false);
    private readonly AutoResetEvent _installNow = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private int _disposed;

    private UnixSocketSignals(Socket listener, ILogger log)
    {
        _listener = listener;
        _log = log;
        _serving = Task.Run(ServeAsync);
    }

    public static string RunFolder(string root) => Path.Combine(root, "run");

    public static string SocketPath(string root) => Path.Combine(RunFolder(root), "control.sock");

    /// <summary>For the daemon (root): opens <c>run/control.sock</c> below <see cref="AppPaths.Root"/>.</summary>
    /// <exception cref="IOException">The folder or socket path is not safe to use, or the socket cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The run folder or a stale socket belongs to another user.</exception>
    public static UnixSocketSignals Start(ILogger log) => Start(AppPaths.Root, RootUserId, log);

    /// <summary>As <see cref="Start(ILogger)"/> below <paramref name="root"/>, insisting on <paramref name="expectedOwner"/>
    /// as the owner of <c>run/</c> and of a stale socket (tests run as an ordinary user).</summary>
    internal static UnixSocketSignals Start(string root, uint expectedOwner, ILogger log)
    {
        if (!UnixNative.IsSupported) throw new PlatformNotSupportedException("The control socket needs macOS on Apple silicon.");
        var runFolder = PrepareRunFolder(root, expectedOwner);
        var socketPath = Path.Combine(runFolder, "control.sock");
        RemoveStaleSocket(socketPath, expectedOwner);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, SocketMode); // run/ is the daemon's own: nobody can swap the entry
            listener.Listen(Backlog);
            return new UnixSocketSignals(listener, log);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    /// <summary>For a client: sends <paramref name="command"/> as one line to the daemon below <paramref name="root"/>.</summary>
    /// <exception cref="IOException">The daemon is not running or did not take the request.</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not connect to the socket.</exception>
    public static void Send(string root, string command)
    {
        if (command is not (CheckCommand or InstallCommand)) throw new ArgumentException($"Unknown command '{command}'.", nameof(command));
        var path = SocketPath(root);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.SendTimeout = (int)RequestTimeout.TotalMilliseconds;
        try
        {
            using (var timeout = new CancellationTokenSource(RequestTimeout))
                socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).AsTask().GetAwaiter().GetResult();
            socket.Send(Encoding.ASCII.GetBytes(command + "\n"));
            // No shutdown: the daemon may already have closed its end after the line, which would make it fail.
            // Closing the socket ends the request just as well.
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException("The Illux Image Tool service did not take the request in time.", ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable || !File.Exists(path))
        {
            throw new IOException(ServiceRequests.NotRunningMessage, ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new UnauthorizedAccessException(ServiceRequests.NotAllowedMessage, ex);
        }
        catch (SocketException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public bool TryWaitCheckNow(TimeSpan timeout) => _checkNow.WaitOne(timeout);

    public bool TryWaitInstallNow(TimeSpan timeout) => _installNow.WaitOne(timeout);

    /// <summary>
    /// Stops listening at once (a connection being read is dropped). Requests sent afterwards fail as "not running";
    /// a socket file left behind is replaced by the next <see cref="Start(ILogger)"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Dispose();
        try { _serving.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _stop.Dispose();
        _checkNow.Dispose();
        _installNow.Dispose();
    }

    /// <summary>Root must be a real folder; <c>run/</c> is created (0755) or must be a real folder owned by <paramref name="owner"/>.</summary>
    private static string PrepareRunFolder(string root, uint owner)
    {
        if (UnixNative.LStat(root) is null) Directory.CreateDirectory(root);
        RequireFolder(root);

        var run = RunFolder(root);
        if (UnixNative.LStat(run) is null) Directory.CreateDirectory(run, RunFolderMode);
        var status = RequireFolder(run);
        if (status.Owner != owner)
            throw new UnauthorizedAccessException($"The folder '{run}' is owned by user {status.Owner}, not {owner}; the control socket is not opened.");
        if (status.Mode != RunFolderMode) File.SetUnixFileMode(run, RunFolderMode); // ours and not a link (checked above)
        return run;
    }

    private static UnixNative.Status RequireFolder(string path)
    {
        var status = UnixNative.LStat(path) ?? throw new IOException($"The folder '{path}' could not be created.");
        if (status.Type == UnixNative.SymbolicLinkType)
            throw new IOException($"'{path}' is a link; the control socket is not opened.");
        if (status.Type != UnixNative.DirectoryType)
            throw new IOException($"'{path}' is not a folder; the control socket is not opened.");
        return status;
    }

    /// <summary>
    /// Removes a socket file a previous daemon left behind. Anything else at that path (no socket, or a socket owned
    /// by another user) is refused and left alone; links are never followed.
    /// </summary>
    internal static void RemoveStaleSocket(string path, uint owner)
    {
        if (UnixNative.LStat(path) is not { } status) return;
        if (status.Type != UnixNative.SocketType)
            throw new IOException($"'{path}' exists and is not a socket; it is left alone and the control socket is not opened.");
        if (status.Owner != owner)
            throw new UnauthorizedAccessException($"The socket '{path}' is owned by user {status.Owner}, not {owner}; it is left alone and the control socket is not opened.");
        File.Delete(path); // unlink(2): removes the entry itself
    }

    private async Task ServeAsync()
    {
        var token = _stop.Token;
        var buffer = new byte[MaxRequestBytes];
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(token).ConfigureAwait(false);
                switch (await ReadCommandAsync(client, buffer, token).ConfigureAwait(false))
                {
                    case CheckCommand:
                        _checkNow.Set();
                        break;
                    case InstallCommand:
                        _installNow.Set();
                        break;
                    default:
                        _log.LogDebug("Ignored a control socket request that was not 'check' or 'install'");
                        break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "The control socket could not take a request; continuing");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// The first line from <paramref name="client"/>, trimmed, when it is a known command; null for anything else,
    /// for no line end within <see cref="MaxRequestBytes"/>, or when the client is too slow.
    /// </summary>
    private static async Task<string?> ReadCommandAsync(Socket client, byte[] buffer, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(RequestTimeout);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length) return null; // no line end within the cap
                var read = await client.ReceiveAsync(buffer.AsMemory(length), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (read == 0) break; // the client is done: what it sent is the line
                var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
                length += read;
                if (newline >= 0)
                {
                    length = newline;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return null; // too slow
        }
        catch (SocketException)
        {
            return null;
        }

        var line = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return line is CheckCommand or InstallCommand ? line : null;
    }
}
