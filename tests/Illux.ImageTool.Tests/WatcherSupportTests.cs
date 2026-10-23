using Illux.ImageTool.Runtime;
using Illux.ImageTool.Watcher;
using Illux.ImageTool.Watcher.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Illux.ImageTool.Tests;

public sealed class WatcherInfoTests
{
    [Theory]
    [InlineData("1.2.3+abcdef0", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.0.0-dev", "0.0.0")]
    [InlineData("2.0.1-beta.1+deadbeef", "2.0.1")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("garbage", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void ParseVersion_StripsCommitAndSuffix(string? input, string expected) =>
        Assert.Equal(Version.Parse(expected), WatcherInfo.ParseVersion(input));

    [Fact]
    public void ServiceNames_AreTheDocumentedOnes()
    {
        Assert.Equal("IlluxImageToolWatcher", WatcherInfo.ServiceName);
        Assert.Equal("Illux Image Tool Watcher", WatcherInfo.DisplayName);
        Assert.Equal("Illux Image Tool", WatcherInfo.EventLogSource);
    }
}

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-filelog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void Log_WritesDailyFile_AndRemovesFilesOlderThanThirtyDays()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "watcher-20260801.log", "watcher-20260923.log", "watcher-20260924.log", "other.log" })
            File.WriteAllText(Path.Combine(_dir, name), "old");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        using var provider = new FileLoggerProvider(() => _dir, clock);

        var log = provider.CreateLogger("Test");
        log.LogInformation("hello {Name}", "world");
        log.LogDebug("not written");

        var today = Path.Combine(_dir, "watcher-20261023.log");
        var text = File.ReadAllText(today);
        Assert.Contains("[INF] Test: hello world", text);
        Assert.DoesNotContain("not written", text);
        Assert.False(File.Exists(Path.Combine(_dir, "watcher-20260801.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "watcher-20260923.log"))); // exactly 30 days back: outside the kept 30
        Assert.True(File.Exists(Path.Combine(_dir, "watcher-20260924.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "other.log")));
    }

    [Fact]
    public void Log_FileHeldOpenByAnotherReader_StillWrites()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        using var provider = new FileLoggerProvider(() => _dir, clock);
        var log = provider.CreateLogger("Test");
        log.LogWarning("first");

        var path = Path.Combine(_dir, "watcher-20261023.log");
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            log.LogError(new InvalidOperationException("boom"), "second");

        var text = File.ReadAllText(path);
        Assert.Contains("[WRN] Test: first", text);
        Assert.Contains("[ERR] Test: second", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Log_ThrowingFormatter_DoesNotThrow()
    {
        using var provider = new FileLoggerProvider(() => _dir);

        provider.CreateLogger("Test").Log(LogLevel.Error, 0, "state", null, (_, _) => throw new InvalidOperationException("formatter"));
    }

    [Fact]
    public void Log_UnwritableFolder_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x"); // a file where the log folder should be
        using var provider = new FileLoggerProvider(() => Path.Combine(blocker, "logs"));

        provider.CreateLogger("Test").LogError("nobody hears this");
    }
}

public sealed class BestEffortLoggerProviderTests
{
    private sealed class ThrowingProvider : ILoggerProvider
    {
        public int Calls { get; private set; }

        public ILogger CreateLogger(string categoryName) => new ThrowingLogger(this);

        public void Dispose() => throw new InvalidOperationException("dispose");

        private sealed class ThrowingLogger(ThrowingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner.Calls++;
                throw new InvalidOperationException("event log is unavailable");
            }
        }
    }

    [Fact]
    public void FailingSink_NeverThrows_AndBelowMinimumIsNotForwarded()
    {
        var inner = new ThrowingProvider();
        using var provider = new BestEffortLoggerProvider(inner, LogLevel.Warning);
        var log = provider.CreateLogger("Test");

        log.LogInformation("below the minimum");
        log.LogWarning("reaches the sink and fails quietly");

        Assert.Equal(1, inner.Calls);
    }
}

[Collection("AppRoot")]
public sealed class StaleUpdatingFlagTests : AppRootTestBase
{
    private static void WriteFlag(TimeSpan age)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.UpdatingFlag, "1.2.3");
        File.SetLastWriteTimeUtc(AppPaths.UpdatingFlag, DateTime.UtcNow - age);
    }

    private static TimeProvider Now => TimeProvider.System;

    [Fact]
    public void OldFlag_NoInstallerRunning_IsRemoved()
    {
        WriteFlag(TimeSpan.FromMinutes(20));

        Assert.True(StaleUpdatingFlag.RemoveIfStale(Now, () => false, NullLogger.Instance));
        Assert.False(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public void RecentFlag_IsKept()
    {
        WriteFlag(TimeSpan.FromMinutes(5));

        Assert.False(StaleUpdatingFlag.RemoveIfStale(Now, () => false, NullLogger.Instance));
        Assert.True(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public void OldFlag_InstallerStillRunning_IsKept()
    {
        WriteFlag(TimeSpan.FromMinutes(20));

        Assert.False(StaleUpdatingFlag.RemoveIfStale(Now, () => true, NullLogger.Instance));
        Assert.True(File.Exists(AppPaths.UpdatingFlag));
    }

    [Fact]
    public void NoFlag_NothingHappens() =>
        Assert.False(StaleUpdatingFlag.RemoveIfStale(Now, () => false, NullLogger.Instance));
}
