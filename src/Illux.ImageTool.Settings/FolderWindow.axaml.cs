using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Illux.ImageTool.Options;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>
/// Add or edit one watched folder. <see cref="Window.ShowDialog{TResult}(Window)"/> returns the edited folder, or null
/// when cancelled. The options are saved as the same parameter strings Automation Engine passes (<see cref="FolderSettings"/>).
/// </summary>
public partial class FolderWindow : Window
{
    private IReadOnlyList<string> _extra = [];
    private bool _unknownOptions;

    public FolderWindow() : this(new WatchedFolder())
    {
    }

    public FolderWindow(WatchedFolder folder)
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        Title = string.IsNullOrEmpty(folder.Name) ? "Add folder" : "Edit folder";

        BrowseIn.Click += async (_, _) => await BrowseAsync(InBox, "In folder");
        BrowseOut.Click += async (_, _) => await BrowseAsync(OutBox, "Out folder");
        BrowseError.Click += async (_, _) => await BrowseAsync(ErrorBox, "Error folder");
        BrowseOriginals.Click += async (_, _) => await BrowseAsync(OriginalsBox, "Originals folder");
        OkButton.Click += async (_, _) => await TryAcceptAsync();
        CancelButton.Click += (_, _) => Close(null);

        LoadFolder(folder);
        Opened += async (_, _) =>
        {
            if (_unknownOptions)
                await MessageWindow.ShowAsync(this,
                    "This folder has options this window does not understand. They are shown as the defaults; press OK to replace them.");
        };
    }

    private void LoadFolder(WatchedFolder folder)
    {
        NameBox.Text = folder.Name;
        InBox.Text = folder.In;
        OutBox.Text = folder.Out;
        ErrorBox.Text = folder.Error;
        OriginalsBox.Text = folder.Originals ?? "";
        EnabledBox.IsChecked = folder.Enabled;

        FolderOptions options;
        try
        {
            options = FolderSettings.FromParameters(folder.Parameters);
        }
        catch (ParameterException)
        {
            options = new FolderOptions();
            _unknownOptions = true;
        }

        EightBitBox.IsChecked = options.EightBit;
        ResampleBox.IsChecked = options.Resample;
        BaselineBox.IsChecked = options.Baseline;
        OutputBox.SelectedIndex = (int)options.Output;
        MissingPpiBox.SelectedIndex = (int)options.MissingPpi;
        QualityBox.Value = Math.Clamp(options.Quality, 1, 100);
        _extra = options.Extra;
    }

    private async Task TryAcceptAsync()
    {
        var options = new FolderOptions(
            EightBitBox.IsChecked == true, ResampleBox.IsChecked == true, BaselineBox.IsChecked == true,
            (OutputMode)Math.Max(OutputBox.SelectedIndex, 0), (MissingPpiPolicy)Math.Max(MissingPpiBox.SelectedIndex, 0),
            (int)Math.Clamp(QualityBox.Value ?? 95, 1, 100))
        { Extra = _extra };

        if (!options.EightBit && !options.Resample && !options.Baseline)
        {
            await MessageWindow.ShowAsync(this, "Pick at least one of 8-bit, Resample and Baseline JPEG.");
            return;
        }

        var folder = new WatchedFolder
        {
            Name = Trimmed(NameBox),
            In = Trimmed(InBox),
            Out = Trimmed(OutBox),
            Error = Trimmed(ErrorBox),
            Originals = string.IsNullOrWhiteSpace(OriginalsBox.Text) ? null : Trimmed(OriginalsBox),
            Enabled = EnabledBox.IsChecked == true,
            Parameters = FolderSettings.ToParameters(options),
        };

        var problem = FolderSettings.Validate(folder);
        if (problem is null)
        {
            try { ParameterParser.Parse(folder.Parameters); }
            catch (ParameterException ex) { problem = ex.Message; }
        }
        if (problem is not null)
        {
            await MessageWindow.ShowAsync(this, problem);
            return;
        }

        Close(folder);
    }

    private static string Trimmed(TextBox box) => (box.Text ?? "").Trim();

    private async Task BrowseAsync(TextBox box, string title)
    {
        var options = new FolderPickerOpenOptions { Title = title };
        var current = Trimmed(box);
        if (current.Length > 0 && Directory.Exists(current))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(current);

        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) box.Text = path;
    }
}
