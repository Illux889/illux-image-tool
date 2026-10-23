using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Illux.ImageTool.Watcher.Logging;

/// <summary>
/// R5: wraps a provider (the Windows event log) so that a failing sink never throws into the caller and only entries
/// at <paramref name="minimumLevel"/> or above reach it.
/// </summary>
public sealed class BestEffortLoggerProvider(ILoggerProvider inner, LogLevel minimumLevel) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        try
        {
            return new Guarded(inner.CreateLogger(categoryName), minimumLevel);
        }
        catch (Exception)
        {
            return NullLogger.Instance;
        }
    }

    public void Dispose()
    {
        try { inner.Dispose(); } catch (Exception) { }
    }

    private sealed class Guarded(ILogger inner, LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try { inner.Log(logLevel, eventId, state, exception, formatter); }
            catch (Exception) { /* the file log is the record of truth */ }
        }
    }
}
