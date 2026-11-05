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

    [Theory]
    [InlineData("IlluxImageTool-Setup-0.2.0", true, true)]
    [InlineData("illuximagetool-setup-0.2.0.tmp", true, true)]
    [InlineData("installer", true, false)]
    [InlineData("/usr/sbin/installer", false, true)]
    [InlineData("installer", false, false)]
    [InlineData("/tmp/installer", false, false)] // a user's program named installer must not hold the flag forever (I3)
    [InlineData("/Users/someone/usr/sbin/installer", false, false)]
    [InlineData("/usr/sbin/Installer", false, false)]
    [InlineData("/usr/sbin/installerd", false, false)]
    [InlineData("IlluxImageTool-Setup-0.2.0", false, false)]
    public void InstallerProcess_IsTheSetupOnWindows_AndUsrSbinInstallerOnMac(string nameOrPath, bool windows, bool expected) =>
        Assert.Equal(expected, StaleUpdatingFlag.IsInstallerProcess(nameOrPath, windows));

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ProcessPath_OnMac_IsTheExecutablesFullPath()
    {
        using var sleep = System.Diagnostics.Process.Start("/bin/sleep", "30")!;
        try
        {
            Assert.Equal("/bin/sleep", StaleUpdatingFlag.ProcessPath(sleep.Id));
            Assert.Null(StaleUpdatingFlag.ProcessPath(-1));
        }
        finally
        {
            sleep.Kill();
        }
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void InstallerRunning_OnMac_AProgramNamedInstallerElsewhere_DoesNotCount()
    {
        Directory.CreateDirectory(Dir);
        var fake = Path.Combine(Dir, "installer");
        File.Copy("/bin/sleep", fake);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var running = System.Diagnostics.Process.Start(fake, "30")!;
        try
        {
            Assert.Equal("installer", running.ProcessName);
            Assert.False(StaleUpdatingFlag.IsInstallerRunning());
        }
        finally
        {
            running.Kill();
        }
    }
}

[Collection("AppRoot")]
public sealed class FileLoggerDefaultFolderTests : AppRootTestBase
{
    [Fact]
    public void ServiceLog_GoesToTheAdminOnlyServiceLogs_NotToLogs()
    {
        using (var provider = new FileLoggerProvider())
            provider.CreateLogger("Test").LogInformation("hello from the service");

        var files = Directory.GetFiles(AppPaths.ServiceLogsDir, FileLoggerProvider.Prefix + "*.log");
        Assert.Single(files);
        Assert.Contains("hello from the service", File.ReadAllText(files[0]));
        Assert.False(Directory.Exists(AppPaths.LogsDir)); // R38
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ServiceLogs_OnMac_AreOwnerOnly()
    {
        Directory.CreateDirectory(AppPaths.ServiceLogsDir);
        File.SetUnixFileMode(AppPaths.ServiceLogsDir, (UnixFileMode)0b111_101_101);

        using (var provider = new FileLoggerProvider())
            provider.CreateLogger("Test").LogInformation("hello from the daemon");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(AppPaths.ServiceLogsDir));
        Assert.Single(Directory.GetFiles(AppPaths.ServiceLogsDir, FileLoggerProvider.Prefix + "*.log"));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ServiceLogs_OnMac_NewFolderIsOwnerOnly()
    {
        using (var provider = new FileLoggerProvider())
            provider.CreateLogger("Test").LogInformation("hello from the daemon");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(AppPaths.ServiceLogsDir));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void FolderLogs_OnMac_KeepTheirReadableMode()
    {
        // V15: the folders job logs into folder-logs/ (0755, so administrators can read it), not into service-logs/.
        var readable = (UnixFileMode)0b111_101_101;
        using (var provider = new FileLoggerProvider(() => AppPaths.FolderLogsDir, folderMode: readable))
            provider.CreateLogger("Test").LogInformation("hello from the folders job");

        Assert.Equal(readable, File.GetUnixFileMode(AppPaths.FolderLogsDir));
        Assert.Contains("hello from the folders job", File.ReadAllText(Assert.Single(Directory.GetFiles(AppPaths.FolderLogsDir))));
        Assert.False(Directory.Exists(AppPaths.ServiceLogsDir));
    }

    [MacFact]
    public void ServiceLogs_OnMac_FolderThatIsALink_IsNotWrittenThrough()
    {
        var elsewhere = Path.Combine(Dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(AppPaths.Root);
        Directory.CreateSymbolicLink(AppPaths.ServiceLogsDir, elsewhere);

        using (var provider = new FileLoggerProvider())
            provider.CreateLogger("Test").LogInformation("hello from the daemon");

        Assert.Empty(Directory.GetFiles(elsewhere));
    }
}

/// <summary>The watcher as launchd runs it on macOS: a plain process (no Windows service, no event log) that SIGTERM stops.</summary>
[Collection("AppRoot")]
public sealed class WatcherHostTests : AppRootTestBase
{
    private System.Diagnostics.Process StartWatcher(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "IlluxImageToolWatcher"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = "/",
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["ILLUX_IMAGETOOL_ROOT"] = AppRoot;
        return System.Diagnostics.Process.Start(psi)!;
    }

    private static string? WaitForLog(string folder, string text, System.Diagnostics.Process watcher, Task<string> errors)
    {
        for (var waited = 0; waited < 30000; waited += 200)
        {
            Thread.Sleep(200);
            if (watcher.HasExited) Assert.Fail("the watcher stopped on its own: " + errors.Result);
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, FileLoggerProvider.Prefix + "*.log") : [];
            if (files.Length != 1) continue;
            var log = File.ReadAllText(files[0]);
            if (log.Contains(text, StringComparison.Ordinal)) return log;
        }
        return null;
    }

    private static void StopWithSigterm(System.Diagnostics.Process watcher, Task<string> output, Task<string> errors)
    {
        using (var kill = System.Diagnostics.Process.Start("kill", ["-TERM", watcher.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            kill.WaitForExit();
        Assert.True(watcher.WaitForExit(20000), "SIGTERM did not stop the watcher");
        Assert.Equal(0, watcher.ExitCode);
        Assert.DoesNotContain("Unhandled", errors.Result + output.Result, StringComparison.Ordinal);
    }

    [MacFact]
    public void Watcher_OnMac_WithoutARole_IsRefusedAndSaysSo()
    {
        using var watcher = StartWatcher();
        var errors = watcher.StandardError.ReadToEndAsync();
        watcher.StandardOutput.ReadToEnd();

        Assert.True(watcher.WaitForExit(20000), "the watcher without a role did not stop");
        Assert.NotEqual(0, watcher.ExitCode);
        Assert.Contains("--role updates", errors.Result);
        // The refusal is also logged (the daemon's log; here the test user owns Root, so it can be written).
        var log = File.ReadAllText(Assert.Single(Directory.GetFiles(AppPaths.ServiceLogsDir, FileLoggerProvider.Prefix + "*.log")));
        Assert.Contains("--role updates", log);
    }

    [MacFact]
    public void Watcher_OnMac_FoldersRole_LogsIntoFolderLogs_AndOpensNoControlSocket()
    {
        using var watcher = StartWatcher("--role", "folders");
        var output = watcher.StandardOutput.ReadToEndAsync();
        var errors = watcher.StandardError.ReadToEndAsync();
        try
        {
            var log = WaitForLog(AppPaths.FolderLogsDir, "Watcher started", watcher, errors);
            Assert.NotNull(log);
            Assert.Contains("role: folders", log);
            Assert.DoesNotContain("request channel", log);
            Assert.False(Directory.Exists(Path.Combine(AppRoot, "run")));
            Assert.False(Directory.Exists(AppPaths.ServiceLogsDir));
            StopWithSigterm(watcher, output, errors);
        }
        finally
        {
            if (!watcher.HasExited) watcher.Kill();
        }
    }

    [MacFact]
    public void Watcher_OnMac_UpdatesRole_RunsAsPlainProcess_LogsToServiceLogs_RunsNoWatchedFolders_AndStopsOnSigterm()
    {
        using var watcher = StartWatcher("--role", "updates");
        var output = watcher.StandardOutput.ReadToEndAsync();
        var errors = watcher.StandardError.ReadToEndAsync();
        try
        {
            // Not root here, so the control socket is refused (Root is not owned by root) and that is logged.
            var log = WaitForLog(AppPaths.ServiceLogsDir, "Could not open the request channel", watcher, errors);
            Assert.NotNull(log);
            Assert.Contains("not 0", log);
            // V15: the root job runs no watched folders.
            Assert.Contains("role: updates", log);
            Assert.DoesNotContain("Watcher started", log);
            Assert.False(Directory.Exists(AppPaths.FolderLogsDir));
            StopWithSigterm(watcher, output, errors);
        }
        finally
        {
            if (!watcher.HasExited) watcher.Kill();
        }
    }
}
