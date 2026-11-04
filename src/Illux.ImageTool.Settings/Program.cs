using System.ComponentModel;
using System.Runtime.Versioning;
using Avalonia;

namespace Illux.ImageTool.Settings;

/// <summary>
/// One executable, several roles. No argument: the settings window. <c>--tray</c>: the tray (Windows) or menu-bar
/// (macOS) helper, one per user. Windows only: <c>--service start|stop|account</c>, the elevated copy that does one
/// service action and exits; the window elevates itself (R34), the tray and the service copy never do.
/// </summary>
internal static class Program
{
    private const string TrayMutexName = @"Local\IlluxImageToolTray";

    [STAThread]
    private static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && ServiceControl.TryParseArguments(args, out var action)) return RunServiceAction(action);

        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            // One tray helper per user: a second one started at log-in exits quietly.
            using var mutex = new Mutex(initiallyOwned: true, TrayMutexName, out var first);
            if (!first) return 0;
            TrayHelper.InstallExceptionHandlers();
            return Run(new Launch(LaunchKind.Tray));
        }

        // Windows: config.json is admin-only (R34): the window runs elevated; the non-elevated copy only starts it.
        if (OperatingSystem.IsWindows() && Elevation.ShouldSelfElevate(args, Elevation.IsElevated()))
        {
            return Elevation.RelaunchElevated()
                ? 0
                : Run(new Launch(LaunchKind.Message, "Illux Image Tool Settings needs administrator rights to change the settings."));
        }

        return Run(new Launch(LaunchKind.Window));
    }

    private static int Run(Launch launch) =>
        AppBuilder.Configure(() => new App { Launch = launch })
            .UsePlatformDetect()
            // The menu-bar helper is a menu-bar icon only, without a Dock icon.
            .With(new MacOSPlatformOptions { ShowInDock = launch.Kind != LaunchKind.Tray })
            .LogToTrace()
            .StartWithClassicDesktopLifetime([]);

    /// <summary>The elevated copy: does exactly one service action and exits.</summary>
    [SupportedOSPlatform("windows")]
    private static int RunServiceAction(ServiceAction action)
    {
        if (action == ServiceAction.Account) return Run(new Launch(LaunchKind.Account));
        try
        {
            if (action == ServiceAction.Start) ServiceControl.Start();
            else ServiceControl.Stop();
            return 0;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            SettingsLog.Write($"Service action {action} failed.", ex);
            return Run(new Launch(LaunchKind.Message, ex.Message, ExitCode: 2));
        }
    }
}
