using System.ComponentModel;
using System.Diagnostics;
using Illux.ImageTool.Cli;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>The settings window. Runs without admin rights; only the service buttons start an elevated copy.</summary>
internal sealed class MainForm : Form
{
    private readonly ListView _folders = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
    private readonly Button _add = new() { Text = "Add...", AutoSize = true };
    private readonly Button _edit = new() { Text = "Edit...", AutoSize = true, Enabled = false };
    private readonly Button _remove = new() { Text = "Remove", AutoSize = true, Enabled = false };

    private readonly Label _serviceStatus = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(0, 6, 12, 0) };
    private readonly Button _start = new() { Text = "Start", AutoSize = true };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true };
    private readonly Button _account = new() { Text = "Change account...", AutoSize = true };

    private readonly Label _installed = new() { AutoSize = true };
    private readonly Label _lastCheck = new() { AutoSize = true };
    private readonly Panel _banner = new() { Dock = DockStyle.Fill, Height = 84, BackColor = Color.FromArgb(0xD8, 0xF0, 0xD8), Visible = false, Padding = new Padding(12) };
    private readonly Label _bannerText = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 13f, FontStyle.Bold), ForeColor = Color.FromArgb(0x1E, 0x5A, 0x1E) };
    private readonly Label _phase = new() { AutoSize = true, ForeColor = Color.FromArgb(0x1E, 0x5A, 0x1E) };
    private readonly Button _updateNow = new() { Text = "Update now", Width = 170, Height = 52, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12f, FontStyle.Bold), Dock = DockStyle.Right };
    private readonly Button _checkNow = new() { Text = "Check for updates", AutoSize = true };
    private readonly Label _updateNote = new() { AutoSize = true };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private WatcherConfig _config = new();
    private bool _configUsable;
    private PendingRequest? _pendingCheck;
    private PendingRequest? _pendingUpdate;

    public MainForm()
    {
        Text = "Illux Image Tool Settings";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 640);
        ClientSize = new Size(760, 700);
        Padding = new Padding(12);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildFoldersGroup(), 0, 0);
        root.Controls.Add(BuildServiceGroup(), 0, 1);
        root.Controls.Add(BuildUpdatesGroup(), 0, 2);
        root.Controls.Add(BuildBottomButtons(), 0, 3);
        Controls.Add(root);

        _timer.Tick += (_, _) => RefreshLive();
        Load += (_, _) =>
        {
            ReloadFolders();
            RefreshLive();
            _timer.Start();
        };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private Control BuildFoldersGroup()
    {
        _folders.Columns.Add("Name", 130);
        _folders.Columns.Add("In folder", 190);
        _folders.Columns.Add("Out folder", 190);
        _folders.Columns.Add("Options", 160);
        _folders.Columns.Add("On", 40);
        _folders.SelectedIndexChanged += (_, _) => _edit.Enabled = _remove.Enabled = _configUsable && _folders.SelectedIndices.Count == 1;
        _folders.DoubleClick += (_, _) => EditSelected();
        _add.Click += (_, _) => AddFolder();
        _edit.Click += (_, _) => EditSelected();
        _remove.Click += (_, _) => RemoveSelected();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        buttons.Controls.AddRange([_add, _edit, _remove]);

        var group = new GroupBox { Text = "Watched folders", Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(_folders);
        group.Controls.Add(buttons);
        return group;
    }

    private Control BuildServiceGroup()
    {
        _start.Click += (_, _) => RunElevated(ServiceAction.Start);
        _stop.Click += (_, _) => RunElevated(ServiceAction.Stop);
        _account.Click += (_, _) => RunElevated(ServiceAction.Account);

        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        row.Controls.AddRange([new Label { Text = "Watcher service:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _serviceStatus, _start, _stop, _account]);
        var note = new Label { Text = "Start, Stop and Change account ask Windows for permission.", AutoSize = true, ForeColor = SystemColors.GrayText };

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.AddRange([row, note]);
        var group = new GroupBox { Text = "Service", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildUpdatesGroup()
    {
        _updateNow.Click += (_, _) => UpdateNow();
        _checkNow.Click += (_, _) => CheckNow();

        var bannerLabels = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        bannerLabels.Controls.AddRange([_bannerText, _phase]);
        _banner.Controls.Add(bannerLabels);
        _banner.Controls.Add(_updateNow);

        var info = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        checkRow.Controls.AddRange([_checkNow, _updateNote]);
        _updateNote.Margin = new Padding(8, 6, 0, 0);
        info.Controls.AddRange([_installed, _lastCheck, checkRow]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        layout.Controls.Add(_banner, 0, 0);
        layout.Controls.Add(info, 0, 1);
        _banner.Margin = new Padding(0, 0, 0, 8);

        var group = new GroupBox { Text = "Updates", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildBottomButtons()
    {
        var logs = new Button { Text = "Open log folder", AutoSize = true };
        var help = new Button { Text = "AE parameter help", AutoSize = true };
        logs.Click += (_, _) => OpenLogFolder();
        help.Click += (_, _) => { using var dialog = new HelpDialog(CliHelp.Text); dialog.ShowDialog(this); };

        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        row.Controls.AddRange([logs, help]);
        return row;
    }

    // ----- watched folders -----

    private void ReloadFolders()
    {
        _folders.Items.Clear();
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
            MessageBox.Show(this, ex.Message + "\r\n\r\nFix or delete the file, then open this window again. Changes are turned off until then.",
                "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        foreach (var folder in _config.Folders)
        {
            var item = new ListViewItem(folder.Name);
            item.SubItems.Add(folder.In);
            item.SubItems.Add(folder.Out);
            item.SubItems.Add(string.Join(" ", folder.Parameters));
            item.SubItems.Add(folder.Enabled ? "Yes" : "No");
            _folders.Items.Add(item);
        }

        _add.Enabled = _configUsable;
        _edit.Enabled = _remove.Enabled = _configUsable && _folders.SelectedIndices.Count == 1;
    }

    private void AddFolder()
    {
        using var dialog = new FolderDialog(new WatchedFolder());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Modify(config =>
        {
            config.Folders.Add(dialog.Result);
            return true;
        });
    }

    private void EditSelected()
    {
        if (!_configUsable || _folders.SelectedIndices.Count != 1) return;
        var index = _folders.SelectedIndices[0];
        if (index >= _config.Folders.Count) return;

        var original = _config.Folders[index];
        using var dialog = new FolderDialog(original);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Modify(config =>
        {
            // The file is re-read first; find the folder again by name and In folder, not by its old position.
            var at = FolderMatch.IndexOf(config.Folders, original.Name, original.In);
            if (at < 0) return false;
            config.Folders[at] = dialog.Result;
            return true;
        });
    }

    private void RemoveSelected()
    {
        if (!_configUsable || _folders.SelectedIndices.Count != 1) return;
        var index = _folders.SelectedIndices[0];
        if (index >= _config.Folders.Count) return;

        var original = _config.Folders[index];
        var name = original.Name;
        var answer = MessageBox.Show(this, $"Stop watching \"{name}\"? Its files are not deleted.", "Illux Image Tool",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        Modify(config =>
        {
            var at = FolderMatch.IndexOf(config.Folders, original.Name, original.In);
            if (at < 0) return false;
            config.Folders.RemoveAt(at);
            return true;
        });
    }

    /// <summary>Re-reads the file first so a change made elsewhere meanwhile is not lost, then saves.</summary>
    private void Modify(Func<WatcherConfig, bool> change)
    {
        try
        {
            var config = ConfigStore.Load();
            if (change(config))
                ConfigStore.Save(config);
            else
                MessageBox.Show(this, "That folder was changed or removed somewhere else. The list was refreshed.", "Illux Image Tool",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not save the settings.\r\n" + ex.Message, "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        ReloadFolders();
    }

    // ----- service -----

    private void RunElevated(ServiceAction action)
    {
        try
        {
            ServiceControl.RunElevated(action);
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, "Could not start the action.\r\n" + ex.Message, "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ----- updates -----

    private void UpdateNow()
    {
        if (!TryWriteFlag(RequestFlags.RequestInstallNow)) return;
        _pendingUpdate = new PendingRequest(DateTimeOffset.Now);
        _updateNow.Enabled = false;
        _phase.Text = "Asking the service to update...";
    }

    private void CheckNow()
    {
        if (!TryWriteFlag(RequestFlags.RequestCheckNow)) return;
        _pendingCheck = new PendingRequest(DateTimeOffset.Now);
        _updateNote.Text = "Checking...";
    }

    private bool TryWriteFlag(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not send the request to the service.\r\n" + ex.Message, "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void RefreshLive()
    {
        var status = ServiceControl.GetStatusText();
        _serviceStatus.Text = status;
        _start.Enabled = status is "Stopped" or "Unknown";
        _stop.Enabled = status is "Running" or "Paused";
        _account.Enabled = status != "Not installed";

        var state = UpdateStateStore.Load();
        var view = UpdateView.From(state, AppVersion.Current, status == "Running");
        _installed.Text = "Installed version: " + AppVersion.Current.ToString(3);
        _lastCheck.Text = "Last check: " + (state.LastCheck is { } last ? last.LocalDateTime.ToString("g") : "never");

        var now = DateTimeOffset.Now;
        if (_pendingCheck is { } check)
        {
            if (check.IsAnsweredByCheckAt(state.LastCheck))
            {
                _pendingCheck = null;
                _updateNote.Text = view.ShowBanner ? "" : "You have the latest version.";
            }
            else if (check.IsExpired(now))
            {
                _pendingCheck = null;
                _updateNote.Text = "No answer from the service. Is it running?";
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

        _banner.Visible = view.ShowBanner;
        _bannerText.Text = view.BannerText;
        _updateNow.Enabled = view.CanUpdate && _pendingUpdate is null;
        _phase.Text = phaseText;
    }

    private void OpenLogFolder()
    {
        if (!Directory.Exists(AppPaths.LogsDir))
        {
            MessageBox.Show(this, "There are no logs yet.", "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"") { UseShellExecute = false });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, "Could not open the log folder.\r\n" + ex.Message, "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
