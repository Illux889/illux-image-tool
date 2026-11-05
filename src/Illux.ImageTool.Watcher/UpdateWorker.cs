using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watcher;

public sealed class UpdateWorkerOptions
{
    /// <summary>Wait before the first periodic check after the service started.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How often the requests are looked at.</summary>
    public TimeSpan SignalPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Opens the request channel (R37) when the worker starts. Null opens the platform's: the named events on Windows
    /// (<see cref="ServiceSignals"/>), the control socket on macOS (<see cref="UnixSocketSignals"/>).
    /// </summary>
    public Func<IServiceSignals>? Signals { get; init; }

    /// <summary>The clock for the age of a stale <c>updating.flag</c> (tests set the file's time instead).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Whether an installer is running, for the stale <c>updating.flag</c> check.</summary>
    public Func<bool> InstallerRunning { get; init; } = StaleUpdatingFlag.IsInstallerRunning;

    /// <summary>
    /// How often an update that did not finish is looked for (final review I3): a stale <c>updating.flag</c>, or the
    /// phase Installing without a flag. Also at start and before every install.
    /// </summary>
    public TimeSpan UnfinishedInstallCheckInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>False stops the periodic checks (R4); "check now" still works. Defaults to <c>CheckForUpdates</c> of config.json.</summary>
    public Func<bool>? PeriodicChecksEnabled { get; init; }
}

/// <summary>
/// Looks for updates at start (after a minute) and every 6 hours, and when "check now" is requested. It installs
/// only when "update now" is requested, which the popup and the settings window do after the user clicks
/// "Update now". The requests (<see cref="IServiceSignals"/>, R37) are only triggers: the release to install always
/// comes from this process's own check. They are auto-reset, so a request is acted on once and none is left over from
/// a time the service was not running. All work happens in one loop, so installs are serialised.
/// </summary>
public sealed class UpdateWorker : BackgroundService
{
    /// <summary>The update-state error after a stale <c>updating.flag</c> was removed.</summary>
    public const string StaleFlagError = "The last update did not finish. Try again.";

    /// <summary>The update-state error when the installer ended without installing (the flag is gone, the phase still says Installing).</summary>
    public const string InstallerEndedError = "The update could not be installed. Try again; the installer log is in the update folder.";

    private readonly Updater _updater;
    private readonly ILogger _log;
    private readonly UpdateWorkerOptions _options;
    private ReleaseInfo? _latest;
    private string? _lastCheckError;
    private bool _lastGoodCheckForUpdates = true;

    public UpdateWorker(Updater updater, ILogger log, UpdateWorkerOptions? options = null)
    {
        _updater = updater;
        _log = log;
        _options = options ?? new UpdateWorkerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var signals = OpenSignals();
        var nextCheck = DateTime.UtcNow + _options.InitialDelay;
        var nextUnfinishedCheck = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextUnfinishedCheck)
                {
                    nextUnfinishedCheck = DateTime.UtcNow + _options.UnfinishedInstallCheckInterval;
                    CheckUnfinishedInstall();
                }

                if (signals?.TryWaitCheckNow(TimeSpan.Zero) == true)
                {
                    _log.LogInformation("Update check requested");
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                }

                if (signals?.TryWaitInstallNow(TimeSpan.Zero) == true)
                {
                    // A second click while a started install waited for idle asks for the same thing.
                    if (await InstallAsync(stoppingToken).ConfigureAwait(false) == InstallOutcome.Started) signals.TryWaitInstallNow(TimeSpan.Zero);
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                }

                if (DateTime.UtcNow >= nextCheck)
                {
                    // Advance first: a failing check must wait for the next interval, not be retried on every loop.
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                    if (PeriodicChecksEnabled()) await CheckAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(_options.SignalPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Update worker loop failed; continuing");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private bool PeriodicChecksEnabled()
    {
        if (_options.PeriodicChecksEnabled is { } configured) return configured();
        try
        {
            _lastGoodCheckForUpdates = ConfigStore.Load().CheckForUpdates;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            // A broken file keeps the last good setting (true until a good one was read).
        }
        return _lastGoodCheckForUpdates;
    }

    /// <summary>An unexpected failure is logged at Error once; an identical repeat goes to Debug. It never escapes.</summary>
    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            _latest = await _updater.CheckAsync(ct).ConfigureAwait(false);
            _lastCheckError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _latest = null;
            var summary = $"{ex.GetType().Name}: {ex.Message}";
            if (summary != _lastCheckError)
            {
                _lastCheckError = summary;
                _log.LogError(ex, "Update check failed unexpectedly");
            }
            else
            {
                _log.LogDebug("Update check failed again: {Summary}", summary);
            }
        }
    }

    private async Task<InstallOutcome?> InstallAsync(CancellationToken ct)
    {
        var release = _latest;
        if (release is null)
        {
            await CheckAsync(ct).ConfigureAwait(false);
            release = _latest;
        }
        if (release is null)
        {
            _log.LogInformation("Update requested, but there is no newer release to install");
            return null;
        }

        _log.LogInformation("Update to {Version} requested by the user", release.Version);
        // A flag left by an installer that died would otherwise refuse every install until the next periodic check.
        CheckUnfinishedInstall();
        var outcome = await _updater.InstallAsync(release, ct).ConfigureAwait(false);
        _log.LogInformation("Update to {Version}: {Outcome}", release.Version, outcome);
        return outcome;
    }

    /// <summary>
    /// Final review I3: an <c>updating.flag</c> left by an installer that died is removed (older than 15 minutes, no
    /// installer running), and an install whose installer ended without installing (no flag, phase still Installing:
    /// the macOS install script removes the flag when the installer fails) is marked as failed. Never throws.
    /// </summary>
    private void CheckUnfinishedInstall()
    {
        try
        {
            if (StaleUpdatingFlag.RemoveIfStale(_options.Time, _options.InstallerRunning, _log))
            {
                _updater.MarkFailed(StaleFlagError);
                return;
            }
            if (File.Exists(AppPaths.UpdatingFlag) || UpdateStateStore.Load().Phase != UpdatePhase.Installing) return;
            if (_options.InstallerRunning()) return;
            _updater.MarkFailed(InstallerEndedError, onlyIf: s => s.Phase == UpdatePhase.Installing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not check for an unfinished update: {Message}", ex.Message);
        }
    }

    /// <summary>The request channel, or null (logged) when it cannot be opened: periodic checks still run.</summary>
    private IServiceSignals? OpenSignals()
    {
        try
        {
            if (_options.Signals is { } open) return open();
            if (OperatingSystem.IsWindows()) return ServiceSignals.Open(_log); // logs an event it cannot create itself
            if (OperatingSystem.IsMacOS()) return UnixSocketSignals.Start(_log);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not open the request channel; the settings app and menu bar cannot reach the service");
            return null;
        }
    }
}
