using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Update;

/// <summary>
/// Spec 11a. <see cref="CheckAsync"/> only records that a newer version exists; nothing is downloaded or run until
/// someone clicks "Update now" and the service calls <see cref="InstallAsync"/>.
/// </summary>
public sealed partial class Updater
{
    public static readonly TimeSpan DefaultRecheckInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultGiveUpAfter = TimeSpan.FromMinutes(30);
    public const long DefaultMaxInstallerBytes = 500L * 1024 * 1024;

    private const int MaxChecksumFileBytes = 4096;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256HexPattern();

    private readonly ReleaseClient _client;
    private readonly HttpClient _http;
    private readonly Version _current;
    private readonly Func<bool> _watcherBusy;
    private readonly Func<string, string, Process?> _startInstaller;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly string _downloadDirectory;
    private readonly bool _trustDownloadDirectory;
    private readonly long _maxInstallerBytes;
    private readonly TimeSpan _recheckInterval;
    private readonly TimeSpan _giveUpAfter;
    private readonly object _stateLock = new();
    private bool _stateDeniedLogged;

    /// <param name="startInstaller">Called with the installer path and its command-line arguments; null means it could not be started.</param>
    /// <param name="timeProvider">Used for the idle waits, so tests can run them fast.</param>
    /// <param name="downloadDirectory">Defaults to <c>update</c> below <see cref="AppPaths.Root"/> (SYSTEM/Administrators only).</param>
    /// <param name="trustDownloadDirectory">
    /// Tests only: skip creating the folder with a restricted ACL and the link/owner checks. The default (false) enforces them.
    /// </param>
    public Updater(
        ReleaseClient client,
        HttpClient http,
        Version current,
        Func<bool> watcherBusy,
        Func<string, string, Process?> startInstaller,
        ILogger log,
        TimeProvider? timeProvider = null,
        string? downloadDirectory = null,
        TimeSpan? recheckInterval = null,
        TimeSpan? giveUpAfter = null,
        bool trustDownloadDirectory = false,
        long maxInstallerBytes = DefaultMaxInstallerBytes)
    {
        _client = client;
        _http = http;
        // The release tag has three parts; an assembly version such as 1.2.3.0 must not look newer than release 1.2.3.
        _current = new Version(Math.Max(current.Major, 0), Math.Max(current.Minor, 0), Math.Max(current.Build, 0));
        _watcherBusy = watcherBusy;
        _startInstaller = startInstaller;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _downloadDirectory = downloadDirectory ?? Path.Combine(AppPaths.Root, "update");
        _trustDownloadDirectory = trustDownloadDirectory;
        _maxInstallerBytes = maxInstallerBytes;
        _recheckInterval = recheckInterval ?? DefaultRecheckInterval;
        _giveUpAfter = giveUpAfter ?? DefaultGiveUpAfter;
    }

    /// <summary>
    /// Asks GitHub for the latest release and records it in <c>update-state.json</c>. Returns the release when it is
    /// newer than the running version, otherwise null. A failed check (offline, rate limit) is logged and leaves the
    /// state untouched. Never downloads or installs anything.
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        ReleaseInfo? latest;
        try
        {
            latest = await _client.GetLatestAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogWarning(ex, "Update check failed");
            return null;
        }

        var newer = latest is not null && latest.Version > _current ? latest : null;
        if (newer is not null)
            _log.LogInformation("Update check: version {Available} is available (running {Current})", newer.Version, _current);
        else if (latest is null)
            _log.LogInformation("Update check: no usable release found (running {Current})", _current);
        else
            _log.LogInformation("Update check: up to date (latest release {Latest}, running {Current})", latest.Version, _current);

        UpdateState(state =>
        {
            state.AvailableVersion = newer?.Version.ToString();
            state.LastCheck = _time.GetUtcNow();
        });
        return newer;
    }

    /// <summary>
    /// Downloads, verifies and starts the installer. Call only after a user click. Cancellation throws
    /// <see cref="OperationCanceledException"/> (state goes back to Idle); every other problem is reported through the
    /// outcome and <c>update-state.json</c>.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(ReleaseInfo release, CancellationToken ct)
    {
        var installerPath = Path.Combine(_downloadDirectory, $"IlluxImageTool-Setup-{release.Version}.exe");
        var partPath = installerPath + ".part";
        var started = false;

        _log.LogInformation("Update to {Version}: downloading", release.Version);
        UpdateState(s => { s.Phase = UpdatePhase.Downloading; s.Error = null; });

        // The folder is vetted before the cleanup scope below exists: nothing may be created, read or deleted in a
        // folder that is not trusted (a junction could point SYSTEM at someone else's files).
        string? directoryProblem;
        try
        {
            directoryProblem = PrepareDownloadDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.LogError(ex, "Could not prepare the update folder");
            directoryProblem = $"The update folder could not be prepared: {ex.Message}";
        }
        if (directoryProblem is not null)
            return Fail(release, InstallOutcome.Failed, directoryProblem);

        try
        {
            DeleteOldInstallers();

            var expected = ParseChecksum(await DownloadChecksumAsync(release.ChecksumUrl, ct).ConfigureAwait(false));
            if (expected is null)
                return Fail(release, InstallOutcome.ChecksumFailed, "The checksum file is not a valid SHA-256 checksum; the update was not installed.");

            var actual = await DownloadInstallerAsync(release.InstallerUrl, partPath, ct).ConfigureAwait(false);
            if (!HashesEqual(expected, actual))
                return Fail(release, InstallOutcome.ChecksumFailed, "The downloaded installer does not match its published checksum and was discarded.");
            File.Move(partPath, installerPath, overwrite: true);

            UpdateState(s => s.Phase = UpdatePhase.WaitingForJobs);
            var waitBegan = _time.GetTimestamp();
            while (true)
            {
                if (!await WaitForIdleAsync(waitBegan, ct).ConfigureAwait(false))
                    return Fail(release, InstallOutcome.Busy, "Jobs kept running, try again");

                var outcome = await TryStartAsync(release, installerPath, expected, ct).ConfigureAwait(false);
                if (outcome is null) continue; // a job began between the idle check and the flag: keep waiting
                started = outcome == InstallOutcome.Started;
                return outcome.Value;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("Update to {Version} cancelled", release.Version);
            UpdateState(s => { s.Phase = UpdatePhase.Idle; s.Error = null; });
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update to {Version} failed", release.Version);
            return Fail(release, InstallOutcome.Failed, ex.Message);
        }
        finally
        {
            // Once the installer is running it needs its own file; in every other case nothing is left behind.
            if (!started)
            {
                TryDelete(partPath);
                TryDelete(installerPath);
            }
        }
    }

    private InstallOutcome Fail(ReleaseInfo release, InstallOutcome outcome, string error)
    {
        _log.LogWarning("Update to {Version} not installed ({Outcome}): {Error}", release.Version, outcome, error);
        UpdateState(s => { s.Phase = UpdatePhase.Failed; s.Error = error; });
        return outcome;
    }

    private string? PrepareDownloadDirectory()
    {
        if (_trustDownloadDirectory || !OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_downloadDirectory);
            return null;
        }
        return DownloadDirectory.PrepareTrusted(_downloadDirectory);
    }

    /// <summary>Removes installers (and partial downloads) left by earlier attempts; one that is still running stays.</summary>
    private void DeleteOldInstallers()
    {
        foreach (var file in Directory.EnumerateFiles(_downloadDirectory, "IlluxImageTool-Setup-*.exe*"))
            TryDelete(file);
    }

    private async Task<string> DownloadChecksumAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxChecksumFileBytes)
            throw new InvalidDataException("The checksum file is unexpectedly large.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxChecksumFileBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
            total += read;
        if (total > MaxChecksumFileBytes)
            throw new InvalidDataException("The checksum file is unexpectedly large.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>The first whitespace-separated token must be 64 hex digits; an optional file name may follow.</summary>
    private static string? ParseChecksum(string content)
    {
        var tokens = content.TrimStart('\uFEFF').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && Sha256HexPattern().IsMatch(tokens[0]) ? tokens[0] : null;
    }

    private static bool HashesEqual(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>Streams the installer to disk and returns its SHA-256 (hex). A failed download deletes the partial file.</summary>
    private async Task<string> DownloadInstallerAsync(Uri url, string path, CancellationToken ct)
    {
        var created = false; // never delete a file this download did not create
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > _maxInstallerBytes)
                throw new InvalidDataException("The installer is larger than the allowed maximum.");

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > _maxInstallerBytes)
                    throw new InvalidDataException("The installer is larger than the allowed maximum.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
        }
    }

    private bool IsIdle() => !RunLock.AnyActive() && !_watcherBusy();

    /// <summary>True once no command-line/AE run and no watched-folder work is active; false after giving up.</summary>
    private async Task<bool> WaitForIdleAsync(long waitBegan, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsIdle()) return true;

            var remaining = _giveUpAfter - _time.GetElapsedTime(waitBegan);
            if (remaining <= TimeSpan.Zero) return false;
            await Task.Delay(remaining < _recheckInterval ? remaining : _recheckInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Locks the installer against writes and deletion, re-hashes it, writes the flag, re-checks idleness and starts
    /// it. The lock is held until the process has started, so the file cannot be swapped after it was verified.
    /// Null means a job started in the meantime (flag removed, caller should wait again).
    /// </summary>
    private async Task<InstallOutcome?> TryStartAsync(ReleaseInfo release, string installerPath, string expectedHash, CancellationToken ct)
    {
        await using var handle = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(handle, ct).ConfigureAwait(false));
        if (!HashesEqual(expectedHash, actual))
            return Fail(release, InstallOutcome.ChecksumFailed, "The installer changed on disk after it was verified and was discarded.");

        // R36: the installer runs as SYSTEM, so its log goes into the vetted download folder (SYSTEM/Administrators
        // only), never into logs\ where users may write and could plant a link under the log's name.
        var logFile = Path.Combine(_downloadDirectory, $"update-{release.Version}.log");
        var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{logFile}\"";
        string? error;
        try
        {
            File.WriteAllText(AppPaths.UpdatingFlag, release.Version.ToString());

            // A run that began just before the flag appeared would not have seen it (AE scripts wait for the flag).
            if (!IsIdle())
            {
                TryDelete(AppPaths.UpdatingFlag);
                return null;
            }

            UpdateState(s => s.Phase = UpdatePhase.Installing);
            using var process = _startInstaller(installerPath, args);
            error = process is null ? "The installer could not be started." : null;
        }
        catch (Exception ex)
        {
            TryDelete(AppPaths.UpdatingFlag); // whatever went wrong, no flag may be left behind
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            _log.LogError(ex, "Could not start the installer");
            error = $"The installer could not be started: {ex.Message}";
        }

        if (error is null)
        {
            _log.LogInformation("Update to {Version}: installer started (log: {LogFile})", release.Version, logFile);
            return InstallOutcome.Started;
        }
        TryDelete(AppPaths.UpdatingFlag);
        return Fail(release, InstallOutcome.Failed, error);
    }

    /// <summary>Read-modify-write so values owned by others (AvailableVersion, or the installer's reset to Idle) are kept.</summary>
    private void UpdateState(Action<UpdateState> change)
    {
        lock (_stateLock)
        {
            try
            {
                if (!UpdateStateStore.TryLoad(out var state))
                {
                    _log.LogWarning("update-state.json could not be read; not overwriting it");
                    return;
                }
                change(state);
                UpdateStateStore.Save(state);
            }
            catch (UnauthorizedAccessException ex)
            {
                // R43: typically a named service account that is not a local Administrator. Said clearly, once.
                if (_stateDeniedLogged) return;
                _stateDeniedLogged = true;
                _log.LogError(ex, "The service account may not write update-state.json in {Root}, so updates cannot be offered or installed. "
                    + "Run the service as Local System or as a local Administrator.", AppPaths.Root);
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "Could not write update-state.json");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
