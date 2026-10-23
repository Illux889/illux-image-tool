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

    /// <summary>How often the request flags are looked for.</summary>
    public TimeSpan FlagPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>False stops the periodic checks (R4); <c>check-now.flag</c> still works. Defaults to <c>CheckForUpdates</c> of config.json.</summary>
    public Func<bool>? PeriodicChecksEnabled { get; init; }
}

/// <summary>
/// Looks for updates at start (after a minute) and every 6 hours, and when <c>check-now.flag</c> appears. It installs
/// only when <c>install-now.flag</c> appears, which the popup and the settings window write after the user clicks
/// "Update now". The flags are only triggers: the release to install always comes from this process's own check.
/// All work happens in one loop, so installs are serialised.
/// </summary>
public sealed class UpdateWorker : BackgroundService
{
    private readonly Updater _updater;
    private readonly ILogger _log;
    private readonly UpdateWorkerOptions _options;
    private ReleaseInfo? _latest;
    private bool _deleteFailureLogged;

    public UpdateWorker(Updater updater, ILogger log, UpdateWorkerOptions? options = null)
    {
        _updater = updater;
        _log = log;
        _options = options ?? new UpdateWorkerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCheck = DateTime.UtcNow + _options.InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (TryConsume(AppPaths.CheckNowFlag))
                {
                    _log.LogInformation("Update check requested");
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                }

                if (TryConsume(AppPaths.InstallNowFlag))
                {
                    await InstallAsync(stoppingToken).ConfigureAwait(false);
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                }

                if (DateTime.UtcNow >= nextCheck)
                {
                    if (PeriodicChecksEnabled()) await CheckAsync(stoppingToken).ConfigureAwait(false);
                    nextCheck = DateTime.UtcNow + _options.CheckInterval;
                }

                await Task.Delay(_options.FlagPollInterval, stoppingToken).ConfigureAwait(false);
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
            return ConfigStore.Load().CheckForUpdates;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            return true; // an unreadable configuration does not turn the update check off
        }
    }

    private async Task CheckAsync(CancellationToken ct) => _latest = await _updater.CheckAsync(ct).ConfigureAwait(false);

    private async Task InstallAsync(CancellationToken ct)
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
            return;
        }

        _log.LogInformation("Update to {Version} requested by the user", release.Version);
        var outcome = await _updater.InstallAsync(release, ct).ConfigureAwait(false);
        _log.LogInformation("Update to {Version}: {Outcome}", release.Version, outcome);

        // A second click while the first install ran asks for the same thing.
        if (outcome == InstallOutcome.Started) TryConsume(AppPaths.InstallNowFlag);
    }

    /// <summary>True when the flag existed and has been deleted (a flag that cannot be deleted would fire again and again).</summary>
    private bool TryConsume(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            _deleteFailureLogged = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_deleteFailureLogged)
            {
                _deleteFailureLogged = true;
                _log.LogWarning(ex, "Could not delete the request flag '{Flag}'; ignoring it until it can be deleted", path);
            }
            return false;
        }
    }
}
