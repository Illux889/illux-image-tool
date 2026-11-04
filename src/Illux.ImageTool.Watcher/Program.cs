using System.Diagnostics;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watcher;
using Illux.ImageTool.Watcher.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

try
{
    // Windows: a Windows service. macOS: a plain process that launchd starts as the daemon and stops with SIGTERM
    // (handled by the default console lifetime); no Windows service or event log APIs there. launchd starts it in
    // "/", so there the content root (where optional configuration files are read and watched) is the app's folder.
    var builder = OperatingSystem.IsWindows()
        ? Host.CreateApplicationBuilder(args)
        : Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });

    if (OperatingSystem.IsWindows())
        builder.Services.AddWindowsService(options => options.ServiceName = WatcherInfo.ServiceName);

    // File log always (service-logs); on Windows also the event log (Warning and above), best effort (R5). The
    // defaults added above are replaced.
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
    builder.Logging.AddProvider(new FileLoggerProvider());
    if (OperatingSystem.IsWindows())
    {
        builder.Logging.AddProvider(new BestEffortLoggerProvider(
            new EventLogLoggerProvider(new EventLogSettings { SourceName = WatcherInfo.EventLogSource }),
            LogLevel.Warning));
    }

    builder.Services.AddSingleton(sp => new WatchWorker(sp.GetRequiredService<ILoggerFactory>().CreateLogger<WatchWorker>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchWorker>());
    builder.Services.AddHostedService(sp =>
    {
        var loggers = sp.GetRequiredService<ILoggerFactory>();
        var http = new HttpClient();
        var updater = new Updater(
            new ReleaseClient(http, ReleasePlatform.Current),
            http,
            WatcherInfo.CurrentVersion,
            () => sp.GetRequiredService<WatchWorker>().Busy,
            Process.Start,
            loggers.CreateLogger<Updater>());
        return new UpdateWorker(updater, loggers.CreateLogger<UpdateWorker>());
    });

    using var host = builder.Build();
    var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    MagickSetup.Configure();
    StaleUpdatingFlag.RemoveIfStale(TimeProvider.System, StaleUpdatingFlag.IsInstallerRunning, startupLog);
    await host.RunAsync();
}
catch (Exception ex)
{
    // The service would otherwise stop with nothing but a generic message in the Windows logs (or launchd's).
    try
    {
        new FileLoggerProvider().CreateLogger("Startup").LogCritical(ex, "The watcher service stopped because of an unhandled error");
    }
    catch (Exception)
    {
        // Best effort only.
    }
    throw;
}
