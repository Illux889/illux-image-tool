using System.Text.Json;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watcher;

public sealed class WatchWorkerOptions
{
    /// <summary>Defaults to <see cref="AppPaths.ConfigFile"/>.</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Tests only: replaces the configured <c>PollSeconds</c>.</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>Tests only: creates the scanner of an enabled folder.</summary>
    public Func<WatchedFolder, FolderScanner>? ScannerFactory { get; init; }
}

/// <summary>
/// Scans every enabled watched folder every <c>PollSeconds</c>. <c>config.json</c> is reloaded when its last-write
/// time or size changes; an invalid file keeps the last good configuration (logged once per change).
/// </summary>
public sealed class WatchWorker : BackgroundService
{
    private readonly ILogger _log;
    private readonly WatchWorkerOptions _options;
    private volatile ScannerEntry[] _scanners = [];
    private WatcherConfig _config = new();
    private (DateTime LastWriteUtc, long Length)? _loadedSignature;
    private string? _lastProblem;

    private sealed record ScannerEntry(string Key, FolderScanner Scanner);

    public WatchWorker(ILogger log, WatchWorkerOptions? options = null)
    {
        _log = log;
        _options = options ?? new WatchWorkerOptions();
    }

    /// <summary>True while any scanner is processing a file.</summary>
    public bool Busy
    {
        get
        {
            foreach (var entry in _scanners)
                if (entry.Scanner.Busy) return true;
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Watcher started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReloadConfigIfChanged();
                foreach (var entry in _scanners)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await entry.Scanner.ScanOnceAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "Scanning a watched folder failed");
                    }
                }
                await Task.Delay(_options.PollInterval ?? TimeSpan.FromSeconds(Math.Max(1, _config.PollSeconds)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Watcher loop failed; continuing");
                await DelayQuietly(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        _log.LogInformation("Watcher stopped");
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private void ReloadConfigIfChanged()
    {
        var path = _options.ConfigPath ?? AppPaths.ConfigFile;
        var signature = ReadSignature(path);
        if (_loadedSignature == signature) return;

        WatcherConfig config;
        try
        {
            config = ConfigStore.Load(path);
        }
        catch (ConfigException ex)
        {
            _loadedSignature = signature; // the same broken file is not read (or reported) again
            ReportProblem($"config.json is not valid; keeping the last good configuration: {ex.Message}");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Possibly a writer holding the file: try again on the next poll, but do not repeat the message.
            ReportProblem($"config.json could not be read; keeping the last good configuration: {ex.Message}");
            return;
        }

        _loadedSignature = signature;
        _lastProblem = null;
        Apply(config);
    }

    private static (DateTime LastWriteUtc, long Length)? ReadSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ReportProblem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        _log.LogError("{Message}", message);
    }

    /// <summary>A folder whose definition did not change keeps its scanner (and with it its state).</summary>
    private void Apply(WatcherConfig config)
    {
        var previous = _scanners.ToList();
        var next = new List<ScannerEntry>();
        foreach (var folder in config.Folders.Where(f => f.Enabled))
        {
            var key = JsonSerializer.Serialize(folder);
            var existing = previous.FirstOrDefault(e => e.Key == key);
            if (existing is not null)
            {
                previous.Remove(existing);
                next.Add(existing);
            }
            else
            {
                next.Add(new ScannerEntry(key, _options.ScannerFactory?.Invoke(folder) ?? new FolderScanner(folder, _log)));
            }
        }

        _config = config;
        _scanners = [.. next];
        _log.LogInformation("Configuration loaded: {Count} watched folder(s) enabled, polling every {Seconds} s", next.Count, config.PollSeconds);
    }
}
