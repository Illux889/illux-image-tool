using System.ComponentModel;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Illux.ImageTool.Settings;

/// <summary>Starts what <see cref="Launch"/> asks for: the settings window, the tray helper, the account question or a message.</summary>
public partial class App : Application
{
    private TrayHelper? _tray;

    internal Launch Launch { get; init; } = new(LaunchKind.Window);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            switch (Launch.Kind)
            {
                case LaunchKind.Window:
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    desktop.MainWindow = new MainWindow();
                    break;
                case LaunchKind.Tray:
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    var memory = TrayMemory.CreateDefault();
                    memory.ForgetLater(); // the tray starts at log-in
                    _tray = new TrayHelper(this, desktop, memory);
                    desktop.Exit += (_, _) => _tray.Dispose();
                    break;
                case LaunchKind.Account when OperatingSystem.IsWindows():
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _ = AskForAccountAsync(desktop);
                    break;
                default:
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    _ = ShowMessageAsync(desktop, Launch.Message, Launch.ExitCode);
                    break;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowMessageAsync(IClassicDesktopStyleApplicationLifetime desktop, string text, int exitCode)
    {
        await MessageWindow.ShowAsync(null, text);
        desktop.Shutdown(exitCode);
    }

    /// <summary>The elevated copy (<c>--service account</c>): asks, changes the account, says how it went. Exit 1 when cancelled.</summary>
    [SupportedOSPlatform("windows")]
    private static async Task AskForAccountAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = new AccountWindow();
        window.Show();
        if (!await window.Answer)
        {
            desktop.Shutdown(1);
            return;
        }

        try
        {
            ServiceControl.ChangeAccount(window.User, window.TakePassword());
            await MessageWindow.ShowAsync(null, "The service account was changed. It is used the next time the service starts.");
            desktop.Shutdown(0);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            SettingsLog.Write("Service action Account failed.", ex);
            await MessageWindow.ShowAsync(null, ex.Message);
            desktop.Shutdown(2);
        }
    }
}
