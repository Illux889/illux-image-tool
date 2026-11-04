using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using Illux.ImageTool.Cli;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The settings window. On Windows it runs elevated (config.json is admin-only, R34) and the service buttons still do
/// their work in a separate elevated copy, so a hung service call cannot freeze the window. On macOS it runs as the
/// user: config.json is written in place by admin-group members only (others see it read-only), and the service
/// buttons ask for an administrator through the standard macOS prompt.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _mayChange = SettingsPlatform.MayChangeSettings;
    private WatcherConfig _config = new();
    private bool _configUsable;
    private bool _refreshing;
    private bool _serviceBusy;
    private PendingRequest? _pendingCheck;
    private PendingRequest? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Get();

        ReadOnlyNote.Text = SettingsPlatform.ReadOnlyNote;
        ReadOnlyNote.IsVisible = !_mayChange;
        AccountButton.IsVisible = AccountNote.IsVisible = SettingsPlatform.IsWindows;
        ServiceLogButton.IsVisible = SettingsPlatform.IsWindows;
        ServiceLogNote.Text = SettingsPlatform.ServiceLogNote;
        ServiceLogNote.IsVisible = !SettingsPlatform.IsWindows;

        FolderList.SelectionChanged += (_, _) => UpdateFolderButtons();
        FolderList.DoubleTapped += async (_, _) => await EditSelectedAsync();
        AddButton.Click += async (_, _) => await AddFolderAsync();
        EditButton.Click += async (_, _) => await EditSelectedAsync();
        RemoveButton.Click += async (_, _) => await RemoveSelectedAsync();
        StartButton.Click += async (_, _) => await RunServiceActionAsync(ServiceAction.Start);
        StopButton.Click += async (_, _) => await RunServiceActionAsync(ServiceAction.Stop);
        AccountButton.Click += async (_, _) => await RunServiceActionAsync(ServiceAction.Account);
        UpdateNowButton.Click += async (_, _) => await UpdateNowAsync();
        CheckNowButton.Click += async (_, _) => await CheckNowAsync();
        LogsButton.Click += async (_, _) => await OpenLogFolderAsync();
        ServiceLogButton.Click += async (_, _) => await ShowServiceLogAsync();
        HelpButton.Click += async (_, _) => await new HelpWindow(CliHelp.Text).ShowDialog(this);

        _timer.Tick += async (_, _) => await RefreshLiveAsync();
        Opened += async (_, _) =>
        {
            await ReloadFoldersAsync();
            await RefreshLiveAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private Task ShowAsync(string text) => MessageWindow.ShowAsync(this, text);

    // ----- watched folders -----

    private async Task ReloadFoldersAsync()
    {
        string? problem = null;
        try
        {
            _config = ConfigStore.Load();
            _configUsable = true;
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            // Never save over a file this window could not read.
            _config = new WatcherConfig();
            _configUsable = false;
            problem = ex.Message;
        }

        FolderList.ItemsSource = _config.Folders.Select(FolderRow.From).ToList();
        AddButton.IsEnabled = _configUsable && _mayChange;
        UpdateFolderButtons();

        if (problem is not null)
            await ShowAsync(problem + "\n\nFix or delete the file, then open this window again. Changes are turned off until then.");
    }

    private void UpdateFolderButtons() =>
        EditButton.IsEnabled = RemoveButton.IsEnabled = _configUsable && _mayChange && FolderList.SelectedIndex >= 0;

    private async Task AddFolderAsync()
    {
        if (!_configUsable || !_mayChange) return;
        var result = await new FolderWindow(new WatchedFolder()).ShowDialog<WatchedFolder?>(this);
        if (result is null) return;
        await ModifyAsync(config =>
        {
            config.Folders.Add(result);
            return true;
        });
    }

    private async Task EditSelectedAsync()
    {
        if (SelectedFolder() is not { } original) return;
        var result = await new FolderWindow(original).ShowDialog<WatchedFolder?>(this);
        if (result is null) return;
        await ModifyAsync(config =>
        {
            // The file is re-read first; find the folder again by name and In folder, not by its old position.
            var at = FolderMatch.IndexOf(config.Folders, original.Name, original.In);
            if (at < 0) return false;
            config.Folders[at] = result;
            return true;
        });
    }

    private async Task RemoveSelectedAsync()
    {
        if (SelectedFolder() is not { } original) return;
        if (!await MessageWindow.AskAsync(this, $"Stop watching \"{original.Name}\"? Its files are not deleted.")) return;
        await ModifyAsync(config =>
        {
            var at = FolderMatch.IndexOf(config.Folders, original.Name, original.In);
            if (at < 0) return false;
            config.Folders.RemoveAt(at);
            return true;
        });
    }

    private WatchedFolder? SelectedFolder()
    {
        if (!_configUsable || !_mayChange) return null;
        var index = FolderList.SelectedIndex;
        return index >= 0 && index < _config.Folders.Count ? _config.Folders[index] : null;
    }

    /// <summary>Re-reads the file first so a change made elsewhere meanwhile is not lost, then saves.</summary>
    private async Task ModifyAsync(Func<WatcherConfig, bool> change)
    {
        try
        {
            var config = ConfigStore.Load();
            if (change(config))
                SettingsPlatform.SaveConfig(config);
            else
                await ShowAsync("That folder was changed or removed somewhere else. The list was refreshed.");
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            await ShowAsync("Could not save the settings.\n" + ex.Message);
        }
        await ReloadFoldersAsync();
    }

    // ----- service -----

    private async Task RunServiceActionAsync(ServiceAction action)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                ServiceControl.RunElevated(action);
            }
            catch (Win32Exception ex)
            {
                await ShowAsync("Could not start the action.\n" + ex.Message);
            }
            return;
        }

        if (OperatingSystem.IsMacOS() && action != ServiceAction.Account) await RunMacServiceActionAsync(action);
    }

    [SupportedOSPlatform("macos")]
    private async Task RunMacServiceActionAsync(ServiceAction action)
    {
        _serviceBusy = true;
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        try
        {
            // osascript waits while the administrator prompt is open: off the UI thread.
            if (await Task.Run(() => MacServiceControl.Run(action)) is { } error) await ShowAsync(error);
        }
        finally
        {
            _serviceBusy = false;
        }
        await RefreshLiveAsync();
    }

    // ----- updates -----

    private async Task UpdateNowAsync()
    {
        if (!await TrySendRequestAsync(ServiceRequests.RequestInstallNow)) return;
        _pendingUpdate = new PendingRequest(DateTimeOffset.Now);
        UpdateNowButton.IsEnabled = false;
        PhaseText.Text = "Asking the service to update...";
    }

    private async Task CheckNowAsync()
    {
        if (!await TrySendRequestAsync(ServiceRequests.RequestCheckNow)) return;
        _pendingCheck = new PendingRequest(DateTimeOffset.Now);
        UpdateNote.Text = "Checking...";
    }

    private delegate bool ServiceRequest(out string? error);

    private async Task<bool> TrySendRequestAsync(ServiceRequest send)
    {
        string? error = null;
        // On macOS a busy daemon is retried for a moment: off the UI thread.
        if (await Task.Run(() => send(out error))) return true;
        await ShowAsync("Could not send the request to the service.\n" + error);
        return false;
    }

    private async Task RefreshLiveAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var status = await Task.Run(SettingsPlatform.ServiceStatusText);
            ShowLive(status, UpdateStateStore.Load());
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ShowLive(string status, UpdateState state)
    {
        ServiceStatus.Text = status;
        StartButton.IsEnabled = !_serviceBusy && status is "Stopped" or "Unknown";
        // macOS: "Unknown" when launchctl could not tell; then both buttons stay usable, as Start does on Windows.
        StopButton.IsEnabled = !_serviceBusy && (status is "Running" or "Paused" || (!SettingsPlatform.IsWindows && status == "Unknown"));
        AccountButton.IsEnabled = status != "Not installed";

        var view = UpdateView.From(state, AppVersion.Current, status == "Running");
        InstalledText.Text = "Installed version: " + AppVersion.Current.ToString(3);
        LastCheckText.Text = "Last check: " + (state.LastCheck is { } last ? last.LocalDateTime.ToString("g") : "never");

        var now = DateTimeOffset.Now;
        if (_pendingCheck is { } check)
        {
            if (check.IsAnsweredByCheckAt(state.LastCheck))
            {
                _pendingCheck = null;
                UpdateNote.Text = view.ShowBanner ? "" : "You have the latest version.";
            }
            else if (check.IsExpired(now))
            {
                _pendingCheck = null;
                UpdateNote.Text = "No answer from the service. Is it running?";
            }
        }

        var phaseText = view.PhaseText;
        if (_pendingUpdate is { } update)
        {
            if (view.Busy || !view.ShowBanner) _pendingUpdate = null;
            else if (update.IsExpired(now))
            {
                _pendingUpdate = null;
                phaseText = "No answer from the service. Is it running?";
            }
            else phaseText = "Asking the service to update...";
        }

        Banner.IsVisible = view.ShowBanner;
        BannerText.Text = view.BannerText;
        UpdateNowButton.IsEnabled = view.CanUpdate && _pendingUpdate is null;
        PhaseText.Text = phaseText;
    }

    // ----- logs and help -----

    /// <summary>
    /// Windows: the service log is admin-only (R38). It is shown here, by this elevated window, rather than opened in
    /// Explorer, which runs as the user and would offer to change the folder's permissions. (On macOS the window only
    /// names the root-only folder.)
    /// </summary>
    private async Task ShowServiceLogAsync()
    {
        var text = ServiceLog.ReadLatest(AppPaths.ServiceLogsDir);
        if (text is null)
        {
            await ShowAsync("There is no service log yet.");
            return;
        }
        await new HelpWindow(text, "Service log").ShowDialog(this);
    }

    /// <summary>The command-line (AE) logs; users may read and write this folder.</summary>
    private async Task OpenLogFolderAsync()
    {
        if (!Directory.Exists(AppPaths.LogsDir))
        {
            await ShowAsync("There are no logs yet.");
            return;
        }
        try
        {
            using var _ = Process.Start(SettingsPlatform.OpenFolderStartInfo(AppPaths.LogsDir));
        }
        catch (Win32Exception ex)
        {
            await ShowAsync("Could not open the log folder.\n" + ex.Message);
        }
    }
}
