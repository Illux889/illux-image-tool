using System.Diagnostics;
using System.Security.Cryptography;
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

    private const int MaxChecksumFileChars = 4096;

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
    private readonly TimeSpan _recheckInterval;
    private readonly TimeSpan _giveUpAfter;
    private readonly object _stateLock = new();

    /// <param name="startInstaller">Called with the installer path and its command-line arguments; null means it could not be started.</param>
    /// <param name="timeProvider">Used for the idle waits, so tests can run them fast.</param>
    /// <param name="downloadDirectory">Defaults to <c>%TEMP%\IlluxImageTool\update</c>.</param>
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
        TimeSpan? giveUpAfter = null)
    {
        _client = client;
        _http = http;
        // The release tag has three parts; an assembly version such as 1.2.3.0 must not look newer than release 1.2.3.
        _current = new Version(Math.Max(current.Major, 0), Math.Max(current.Minor, 0), Math.Max(current.Build, 0));
        _watcherBusy = watcherBusy;
        _startInstaller = startInstaller;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "IlluxImageTool", "update");
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
        try
        {
            UpdateState(s => { s.Phase = UpdatePhase.Downloading; s.Error = null; });
            Directory.CreateDirectory(_downloadDirectory);

            var expected = ParseChecksum(await DownloadChecksumAsync(release.ChecksumUrl, ct).ConfigureAwait(false));
            if (expected is null)
                return Fail(InstallOutcome.ChecksumFailed, "The checksum file is not a valid SHA-256 checksum; the update was not installed.");

            var actual = await DownloadInstallerAsync(release.InstallerUrl, partPath, ct).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return Fail(InstallOutcome.ChecksumFailed, "The downloaded installer does not match its published checksum and was discarded.");
            File.Move(partPath, installerPath, overwrite: true);

            UpdateState(s => s.Phase = UpdatePhase.WaitingForJobs);
            if (!await WaitForIdleAsync(ct).ConfigureAwait(false))
                return Fail(InstallOutcome.Busy, "Jobs kept running, try again");

            started = StartInstaller(installerPath, release.Version);
            return started
                ? InstallOutcome.Started
                : InstallOutcome.Failed; // StartInstaller already recorded the reason
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            UpdateState(s => { s.Phase = UpdatePhase.Idle; s.Error = null; });
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update to {Version} failed", release.Version);
            return Fail(InstallOutcome.Failed, ex.Message);
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

    private InstallOutcome Fail(InstallOutcome outcome, string error)
    {
        UpdateState(s => { s.Phase = UpdatePhase.Failed; s.Error = error; });
        return outcome;
    }

    private async Task<string> DownloadChecksumAsync(Uri url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return text.Length > MaxChecksumFileChars ? "" : text;
    }

    /// <summary>The first whitespace-separated token must be 64 hex digits; an optional file name may follow.</summary>
    private static string? ParseChecksum(string content)
    {
        var tokens = content.TrimStart('﻿').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && Sha256HexPattern().IsMatch(tokens[0]) ? tokens[0] : null;
    }

    /// <summary>Streams the installer to disk and returns its SHA-256 (hex). A failed download deletes the partial file.</summary>
    private async Task<string> DownloadInstallerAsync(Uri url, string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>True once no command-line/AE run and no watched-folder work is active; false after giving up.</summary>
    private async Task<bool> WaitForIdleAsync(CancellationToken ct)
    {
        var begun = _time.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!RunLock.AnyActive() && !_watcherBusy()) return true;

            var remaining = _giveUpAfter - _time.GetElapsedTime(begun);
            if (remaining <= TimeSpan.Zero) return false;
            await Task.Delay(remaining < _recheckInterval ? remaining : _recheckInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the flag, then starts the installer. On any failure the flag is removed and the state says why.</summary>
    private bool StartInstaller(string installerPath, Version version)
    {
        var logFile = Path.Combine(AppPaths.LogsDir, $"update-{version}.log");
        var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{logFile}\"";
        string? error;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.WriteAllText(AppPaths.UpdatingFlag, version.ToString());
            UpdateState(s => s.Phase = UpdatePhase.Installing);

            using var process = _startInstaller(installerPath, args);
            error = process is null ? "The installer could not be started." : null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start the installer");
            error = $"The installer could not be started: {ex.Message}";
        }

        if (error is null) return true;
        TryDelete(AppPaths.UpdatingFlag);
        Fail(InstallOutcome.Failed, error);
        return false;
    }

    /// <summary>Read-modify-write so values owned by others (AvailableVersion, or the installer's reset to Idle) are kept.</summary>
    private void UpdateState(Action<UpdateState> change)
    {
        lock (_stateLock)
        {
            try
            {
                var state = UpdateStateStore.Load();
                change(state);
                UpdateStateStore.Save(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
