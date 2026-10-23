using System.Diagnostics;
using Illux.ImageTool.Update;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The tray helper (<c>--tray</c>): an icon, and a small popup when the service reports a new version.
/// It only ever sends the "update now" request (R37) after the user clicks "Update now".
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)PollInterval.TotalMilliseconds };
    private readonly TrayMemory _memory;
    private string? _shownVersion;
    private UpdatePopup? _popup;

    public TrayApp(TrayMemory memory)
    {
        _memory = memory;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Check for updates", null, (_, _) => CheckNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Illux Image Tool",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenSettings();

        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    /// <summary>Logs unexpected errors (best effort) and keeps the tray alive where Windows allows it. Call before creating any window.</summary>
    public static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => SettingsLog.Write("Unexpected error in the tray helper.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SettingsLog.Write("Fatal error in the tray helper.", e.ExceptionObject as Exception);
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        return SystemIcons.Application;
    }

    private void Poll()
    {
        if (_popup is { IsDisposed: false }) return;

        var version = TrayPolicy.VersionToAnnounce(UpdateStateStore.Load(), AppVersion.Current, _memory, _shownVersion);
        if (version is null) return;

        _shownVersion = version;
        _popup = new UpdatePopup(version, _memory);
        _popup.FormClosed += (_, _) => _popup = null;
        _popup.Show();
    }

    private void CheckNow()
    {
        try
        {
            ServiceRequests.RequestCheckNow();
            _icon.ShowBalloonTip(3000, "Illux Image Tool", "Checking for updates...", ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _icon.ShowBalloonTip(5000, "Illux Image Tool", "Could not send the request: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private static void OpenSettings()
    {
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _popup?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>"Illux Image Tool X.Y.Z is available." with Update now and Later; after Update now it shows the progress.</summary>
internal sealed class UpdatePopup : Form
{
    private readonly string _version;
    private readonly TrayMemory _memory;
    private readonly Label _text = new() { AutoSize = true, MaximumSize = new Size(330, 0), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f) };
    private readonly Button _updateNow = new() { Text = "Update now", Width = 130, Height = 36, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold) };
    private readonly Button _later = new() { Text = "Later", Width = 90, Height = 36 };
    private readonly Button _close = new() { Text = "Close", Width = 90, Height = 36, Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    public UpdatePopup(string version, TrayMemory memory)
    {
        _version = version;
        _memory = memory;

        Text = "Illux Image Tool";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        _text.Text = $"Illux Image Tool {version} is available.";
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([_updateNow, _later, _close]);
        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.AddRange([_text, buttons]);
        Controls.Add(layout);

        _updateNow.Click += (_, _) => StartUpdate();
        _later.Click += (_, _) =>
        {
            _memory.MarkLater(_version);
            Close();
        };
        _close.Click += (_, _) => Close();
        _timer.Tick += (_, _) => ShowProgress();
        FormClosed += (_, _) => _timer.Dispose();

        Shown += (_, _) =>
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        };
    }

    private void StartUpdate()
    {
        try
        {
            ServiceRequests.RequestInstallNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _text.Text = "Could not send the request: " + ex.Message;
            return;
        }

        _updateNow.Visible = _later.Visible = false;
        _close.Visible = true;
        _text.Text = "Asking the service to update...";
        _timer.Start();
    }

    private void ShowProgress()
    {
        var view = UpdateView.From(UpdateStateStore.Load(), AppVersion.Current);
        if (view.PhaseText.Length > 0) _text.Text = view.PhaseText;
        else if (!view.ShowBanner) _text.Text = $"Illux Image Tool {_version} is installed.";
    }
}
