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

    /// <summary>How often the request events are looked at.</summary>
    public TimeSpan SignalPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Name of the "check now" event (R37); tests use a <c>Local\</c> name.</summary>
    public string CheckNowSignal { get; init; } = ServiceSignals.CheckNowName;

    /// <summary>Name of the "update now" event (R37); tests use a <c>Local\</c> name.</summary>
    public string InstallNowSignal { get; init; } = ServiceSignals.InstallNowName;

    /// <summary>False stops the periodic checks (R4); "check now" still works. Defaults to <c>CheckForUpdates</c> of config.json.</summary>
    public Func<bool>? PeriodicChecksEnabled { get; init; }
}

/// <summary>
/// Looks for updates at start (after a minute) and every 6 hours, and when the "check now" event is set. It installs
/// only when the "update now" event is set, which the popup and the settings window do after the user clicks
/// "Update now". The events (R37) are only triggers: the release to install always comes from this process's own
/// check. They are auto-reset, so a request is acted on once and none is left over from a time the service was not
/// running. All work happens in one loop, so installs are serialised.
/// </summary>
public sealed class UpdateWorker : BackgroundService
{
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
        using var checkNow = CreateSignal(_options.CheckNowSignal);
        using var installNow = CreateSignal(_options.InstallNowSignal);
        var nextCheck = DateTime.UtcNow + _options.InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsSet(checkNow))
                {
                    _log.LogInformation("Update check requested");
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                }

                if (IsSet(installNow))
                {
                    // A second click while a started install waited for idle asks for the same thing.
                    if (await InstallAsync(stoppingToken).ConfigureAwait(false) == InstallOutcome.Started) IsSet(installNow);
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
        var outcome = await _updater.InstallAsync(release, ct).ConfigureAwait(false);
        _log.LogInformation("Update to {Version}: {Outcome}", release.Version, outcome);
        return outcome;
    }

    /// <summary>The event for <paramref name="name"/>, or null (logged) when it cannot be created: periodic checks still run.</summary>
    private WaitHandle? CreateSignal(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ServiceSignals.Create(name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not create the request event '{Name}'; the settings window and tray cannot reach the service", name);
            return null;
        }
    }

    /// <summary>True (once) when the auto-reset event was set since the last look.</summary>
    private static bool IsSet(WaitHandle? signal) => signal?.WaitOne(0) == true;
}
