using System.Diagnostics;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watcher;
using Illux.ImageTool.Watcher.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

// V15: Windows runs one service that does everything. macOS runs two launchd jobs with the same program: the root job
// (--role updates: control socket and updates) and the job of the folder account (--role folders: watched folders).
if (WatcherRoles.Parse(args, OperatingSystem.IsWindows(), out var roleError) is not { } role)
{
    var refusal = $"The watcher does not run: {roleError}";
    try { Console.Error.WriteLine(refusal); } catch (Exception) { /* no console */ }
    try
    {
        using var refusalLog = new FileLoggerProvider();
        refusalLog.CreateLogger("Startup").LogCritical("{Refusal}", refusal);
    }
    catch (Exception)
    {
        // Best effort only.
    }
    return 2;
}

// The folders job logs into folder-logs/ (its own, 0755 so administrators can read it); everything else into the
// root-only service-logs/.
var fileLog = role == WatcherRole.Folders
    ? new FileLoggerProvider(() => AppPaths.FolderLogsDir, folderMode: (UnixFileMode)0b111_101_101)
    : new FileLoggerProvider();

try
{
    // Windows: a Windows service. macOS: a plain process that launchd starts as the daemon and stops with SIGTERM
    // (handled by the default console lifetime); no Windows service or event log APIs there. launchd starts it in
    // "/", so there the content root (where optional configuration files are read and watched) is the app's folder.
    var builder = OperatingSystem.IsWindows()
        ? Host.CreateApplicationBuilder(args)
        : Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });

    if (OperatingSystem.IsWindows())
        builder.Services.AddWindowsService(options => options.ServiceName = WatcherInfo.ServiceName);

    // File log always; on Windows also the event log (Warning and above), best effort (R5). The defaults added above
    // are replaced.
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddProvider(fileLog);
    if (OperatingSystem.IsWindows())
    {
        builder.Logging.AddProvider(new BestEffortLoggerProvider(
            new EventLogLoggerProvider(new EventLogSettings { SourceName = WatcherInfo.EventLogSource }),
            LogLevel.Warning));
    }

    if (WatcherRoles.RunsFolders(role))
    {
        builder.Services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<WatchWorker>();
            // The macOS folders job: the updater runs in the root job and sees this work only through runs/ (R30).
            var runLock = role == WatcherRole.Folders ? new FolderRunLock(log) : null;
            // There each file is also staged in the account's private folder-work/ instead of a temporary folder in Out.
            return new WatchWorker(log, new WatchWorkerOptions
            {
                RunMarker = runLock is null ? null : runLock.TryAcquire,
                StagingFolder = role == WatcherRole.Folders ? AppPaths.FolderWorkDir : null,
            });
        });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchWorker>());
    }

    if (WatcherRoles.RunsUpdates(role))
    {
        builder.Services.AddHostedService(sp =>
        {
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var http = new HttpClient();
            // On macOS the watched folders run in the other job: there RunLock (runs/) tells the updater about them.
            Func<bool> foldersBusy = role == WatcherRole.All ? () => sp.GetRequiredService<WatchWorker>().Busy : () => false;
            var updater = new Updater(
                new ReleaseClient(http, ReleasePlatform.Current),
                http,
                WatcherInfo.CurrentVersion,
                foldersBusy,
                Process.Start,
                loggers.CreateLogger<Updater>());
            return new UpdateWorker(updater, loggers.CreateLogger<UpdateWorker>());
        });
    }

    using var host = builder.Build();
    var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLog.LogInformation("Illux Image Tool watcher {Version} is running (role: {Role})", WatcherInfo.CurrentVersion, WatcherRoles.Name(role));
    if (WatcherRoles.RunsFolders(role))
    {
        MagickSetup.Configure();
        // I2: said once here; every file is then refused with the same reason until it is fixed.
        if (MagickSetup.TempDirectoryProblem() is { } tempProblem) startupLog.LogError("{Problem}", tempProblem);
    }
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // The service would otherwise stop with nothing but a generic message in the Windows logs (or launchd's).
    try
    {
        fileLog.CreateLogger("Startup").LogCritical(ex, "The watcher service stopped because of an unhandled error");
    }
    catch (Exception)
    {
        // Best effort only.
    }
    throw;
}
