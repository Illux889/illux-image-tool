using System.Globalization;
using System.Text;
using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watcher.Logging;

/// <summary>
/// Daily log file <c>logs\watcher-YYYYMMDD.log</c>, 30 days kept. The file is opened per entry with
/// <see cref="FileShare.ReadWrite"/> so the settings window can read it and other writers can append; a logging
/// failure never reaches the caller.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string Prefix = "watcher-";
    public const string DateFormat = "yyyyMMdd";
    public const int KeepDays = 30;

    private readonly Func<string> _directory;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private DateOnly _lastCleanup;

    public FileLoggerProvider(Func<string>? directory = null, TimeProvider? clock = null)
    {
        _directory = directory ?? (() => AppPaths.LogsDir);
        _clock = clock ?? TimeProvider.System;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        try
        {
            var now = _clock.GetLocalNow();
            var text = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(Abbreviate(level)).Append("] ")
                .Append(category).Append(": ").Append(message).AppendLine();
            if (exception is not null) text.AppendLine(exception.ToString());

            lock (_gate)
            {
                var directory = _directory();
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, Prefix + now.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log");
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
                    stream.Write(bytes, 0, bytes.Length);
                }

                var today = DateOnly.FromDateTime(now.DateTime);
                if (today != _lastCleanup)
                {
                    _lastCleanup = today;
                    DeleteOldFiles(directory, today);
                }
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    private static void DeleteOldFiles(string directory, DateOnly today)
    {
        foreach (var file in Directory.EnumerateFiles(directory, Prefix + "*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            if (!DateOnly.TryParseExact(stamp, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (date > today.AddDays(-KeepDays)) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
