using System.Diagnostics;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watcher;
using Illux.ImageTool.Watcher.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = WatcherInfo.ServiceName);

// File log always; the event log (Warning and above) is best effort (R5). The defaults added above are replaced.
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
        new ReleaseClient(http),
        http,
        WatcherInfo.CurrentVersion,
        () => sp.GetRequiredService<WatchWorker>().Busy,
        (path, installerArgs) => Process.Start(new ProcessStartInfo(path, installerArgs) { UseShellExecute = false }),
        loggers.CreateLogger<Updater>());
    return new UpdateWorker(updater, loggers.CreateLogger<UpdateWorker>());
});

using var host = builder.Build();
var startupLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
MagickSetup.Configure();
StaleUpdatingFlag.RemoveIfStale(TimeProvider.System, StaleUpdatingFlag.IsInstallerRunning, startupLog);
await host.RunAsync();
