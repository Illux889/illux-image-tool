using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The tray helper (<c>--tray</c>): an icon in the Windows tray or the macOS menu bar, and a small popup when the
/// service reports a new version. It runs as the user and never elevates (R34); it only ever sends the "update now"
/// request (R37) after the user clicks "Update now".
/// </summary>
internal sealed class TrayHelper : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly TrayMemory _memory;
    private readonly TrayIcon _icon;
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private string? _shownVersion;
    private UpdatePopup? _popup;

    public TrayHelper(Application app, IClassicDesktopStyleApplicationLifetime desktop, TrayMemory memory)
    {
        _desktop = desktop;
        _memory = memory;

        var menu = new NativeMenu();
        menu.Items.Add(MenuItem("Open settings", OpenSettings));
        menu.Items.Add(MenuItem("Check for updates", () => _ = CheckNowAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("Exit", () => _desktop.Shutdown()));

        _icon = new TrayIcon { Icon = AppIcon.Get(), ToolTipText = "Illux Image Tool", Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) => OpenSettings(); // Windows: a click on the icon (on macOS a click opens the menu)
        TrayIcon.SetIcons(app, [_icon]);

        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    /// <summary>Logs unexpected errors (best effort) and keeps the tray alive where possible. Call before showing anything.</summary>
    public static void InstallExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            SettingsLog.Write("Unexpected error in the tray helper.", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SettingsLog.Write("Fatal error in the tray helper.", e.ExceptionObject as Exception);
    }

    private static NativeMenuItem MenuItem(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    private void Poll()
    {
        if (_popup is not null) return;

        var version = TrayPolicy.VersionToAnnounce(UpdateStateStore.Load(), AppVersion.Current, _memory, _shownVersion);
        if (version is null) return;

        _shownVersion = version;
        _popup = new UpdatePopup(version, _memory);
        _popup.Closed += (_, _) => _popup = null;
        _popup.Show();
    }

    private static async Task CheckNowAsync()
    {
        string? error = null;
        // On macOS a busy daemon is retried for a moment: off the UI thread.
        var text = await Task.Run(() => ServiceRequests.RequestCheckNow(out error))
            ? "Checking for updates..."
            : "Could not send the request: " + error;
        await MessageWindow.ShowAsync(null, text);
    }

    private static void OpenSettings()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            SettingsLog.Write("Could not open the settings window.", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.IsVisible = false;
        _icon.Dispose();
        _popup?.Close();
    }
}
