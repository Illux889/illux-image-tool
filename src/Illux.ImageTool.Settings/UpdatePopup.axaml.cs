using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Update;

namespace Illux.ImageTool.Settings;

/// <summary>
/// "Illux Image Tool X.Y.Z is available." with Update now and Later; after Update now it shows the progress. Shown by
/// the tray helper near the tray (Windows) or below the menu bar (macOS). It only ever sends the "update now" request
/// after the user clicked "Update now" (R37).
/// </summary>
public partial class UpdatePopup : Window
{
    private readonly string _version;
    private readonly TrayMemory? _memory;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public UpdatePopup() : this("", null)
    {
    }

    internal UpdatePopup(string version, TrayMemory? memory)
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        _version = version;
        _memory = memory;
        Message.Text = $"Illux Image Tool {version} is available.";

        UpdateNowButton.Click += async (_, _) => await StartUpdateAsync();
        LaterButton.Click += (_, _) =>
        {
            _memory?.MarkLater(_version);
            Close();
        };
        CloseButton.Click += (_, _) => Close();
        _timer.Tick += (_, _) => ShowProgress();
        Closed += (_, _) => _timer.Stop();
        Opened += (_, _) => PlaceInCorner();
    }

    /// <summary>Bottom right of the work area on Windows (next to the tray), top right on macOS (below the menu bar).</summary>
    private void PlaceInCorner()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        var margin = (int)(16 * screen.Scaling);
        var x = area.Right - size.Width - margin;
        var y = OperatingSystem.IsMacOS() ? area.Y + margin : area.Bottom - size.Height - margin;
        Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
    }

    private async Task StartUpdateAsync()
    {
        UpdateNowButton.IsEnabled = false;
        string? error = null;
        // On macOS a busy daemon is retried for a moment: off the UI thread.
        var sent = await Task.Run(() => ServiceRequests.RequestInstallNow(out error));
        UpdateNowButton.IsEnabled = true;
        if (!sent)
        {
            Message.Text = "Could not send the request: " + error;
            return;
        }

        UpdateNowButton.IsVisible = LaterButton.IsVisible = false;
        CloseButton.IsVisible = true;
        Message.Text = "Asking the service to update...";
        _timer.Start();
    }

    private void ShowProgress()
    {
        var view = UpdateView.From(UpdateStateStore.Load(), AppVersion.Current);
        if (view.PhaseText.Length > 0) Message.Text = view.PhaseText;
        else if (!view.ShowBanner) Message.Text = $"Illux Image Tool {_version} is installed.";
    }
}
