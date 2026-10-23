using Illux.ImageTool.Options;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>Add or edit one watched folder. The options are saved as the same parameter strings Automation Engine passes.</summary>
internal sealed class FolderDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _in = new() { Dock = DockStyle.Fill };
    private readonly TextBox _out = new() { Dock = DockStyle.Fill };
    private readonly TextBox _error = new() { Dock = DockStyle.Fill };
    private readonly TextBox _originals = new() { Dock = DockStyle.Fill, PlaceholderText = "Empty: an Originals folder next to In" };
    private readonly CheckBox _enabled = new() { Text = "Watch this folder", AutoSize = true, Checked = true };
    private readonly CheckBox _eightBit = new() { Text = "8-bit", AutoSize = true };
    private readonly CheckBox _upsample = new() { Text = "Upsample to", AutoSize = true };
    private readonly NumericUpDown _ppi = new() { Minimum = 1, Maximum = 2400, Value = 200, Width = 70 };
    private readonly CheckBox _baseline = new() { Text = "Baseline JPEG", AutoSize = true };
    private readonly ComboBox _output = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ComboBox _missingPpi = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _quality = new() { Minimum = 1, Maximum = 100, Value = 95, Width = 70 };
    private IReadOnlyList<string> _extra = [];

    public FolderDialog(WatchedFolder folder)
    {
        Text = string.IsNullOrEmpty(folder.Name) ? "Add folder" : "Edit folder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _output.Items.AddRange(["Same (keep the format)", "PDF", "Both (file and PDF)"]);
        _missingPpi.Items.AddRange(["Assume 72 ppi (warning)", "Skip the file", "Error"]);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddTextRow(table, "Name", _name, browse: false);
        AddTextRow(table, "In folder", _in, browse: true);
        AddTextRow(table, "Out folder", _out, browse: true);
        AddTextRow(table, "Error folder", _error, browse: true);
        AddTextRow(table, "Originals folder", _originals, browse: true);

        var upsampleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        upsampleRow.Controls.AddRange([_upsample, _ppi, new Label { Text = "ppi", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }]);
        var operations = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        operations.Controls.AddRange([_eightBit, upsampleRow, _baseline]);
        AddRow(table, "Do", operations);

        AddRow(table, "Output", _output);
        AddRow(table, "If ppi is missing", _missingPpi);
        var quality = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        quality.Controls.AddRange([_quality, new Label { Text = "(1 to 100)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }]);
        AddRow(table, "JPEG quality", quality);
        AddRow(table, "", _enabled);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => TryAccept();
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        table.Controls.Add(buttons, 1, table.RowCount++);
        table.SetColumnSpan(buttons, 2);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadFolder(folder);
    }

    /// <summary>The edited values; read after the dialog returned <see cref="DialogResult.OK"/>.</summary>
    public WatchedFolder Result { get; private set; } = new();

    private void LoadFolder(WatchedFolder folder)
    {
        _name.Text = folder.Name;
        _in.Text = folder.In;
        _out.Text = folder.Out;
        _error.Text = folder.Error;
        _originals.Text = folder.Originals ?? "";
        _enabled.Checked = folder.Enabled;

        FolderOptions options;
        try
        {
            options = FromParameters(folder.Parameters);
        }
        catch (ParameterException)
        {
            options = new FolderOptions();
            MessageBox.Show(this, "This folder has options this window does not understand. They are shown as the defaults; press OK to replace them.",
                "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        _eightBit.Checked = options.EightBit;
        _upsample.Checked = options.Upsample;
        _ppi.Value = Math.Clamp(options.Ppi, (int)_ppi.Minimum, (int)_ppi.Maximum);
        _baseline.Checked = options.Baseline;
        _output.SelectedIndex = (int)options.Output;
        _missingPpi.SelectedIndex = (int)options.MissingPpi;
        _quality.Value = Math.Clamp(options.Quality, (int)_quality.Minimum, (int)_quality.Maximum);
        _extra = options.Extra;
    }

    private void TryAccept()
    {
        var options = new FolderOptions(
            _eightBit.Checked, _upsample.Checked, (int)_ppi.Value, _baseline.Checked,
            (OutputMode)_output.SelectedIndex, (MissingPpiPolicy)_missingPpi.SelectedIndex, (int)_quality.Value)
        { Extra = _extra };

        if (!options.EightBit && !options.Upsample && !options.Baseline)
        {
            Warn("Pick at least one of 8-bit, Upsample and Baseline JPEG.");
            return;
        }

        var folder = new WatchedFolder
        {
            Name = _name.Text.Trim(),
            In = _in.Text.Trim(),
            Out = _out.Text.Trim(),
            Error = _error.Text.Trim(),
            Originals = string.IsNullOrWhiteSpace(_originals.Text) ? null : _originals.Text.Trim(),
            Enabled = _enabled.Checked,
            Parameters = ToParameters(options),
        };

        var problem = Validate(folder);
        if (problem is null)
        {
            try { ParameterParser.Parse(folder.Parameters); }
            catch (ParameterException ex) { problem = ex.Message; }
        }
        if (problem is not null)
        {
            Warn(problem);
            return;
        }

        Result = folder;
        DialogResult = DialogResult.OK;
    }

    private void Warn(string text) => MessageBox.Show(this, text, "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        table.Controls.Add(control, 1, row);
        table.SetColumnSpan(control, 2);
    }

    private static void AddTextRow(TableLayoutPanel table, string label, TextBox box, bool browse)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        table.Controls.Add(box, 1, row);
        if (!browse) return;

        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : "", UseDescriptionForTitle = true, Description = label };
            if (dialog.ShowDialog(table.FindForm()) == DialogResult.OK) box.Text = dialog.SelectedPath;
        };
        table.Controls.Add(button, 2, row);
    }

    /// <summary>The parameter strings for the options. At least one operation must be on, otherwise the parser would read "none" as "all".</summary>
    internal static List<string> ToParameters(FolderOptions options)
    {
        if (!options.EightBit && !options.Upsample && !options.Baseline)
            throw new InvalidOperationException("At least one operation must be on.");

        var parameters = new List<string>();
        if (options.EightBit) parameters.Add("8bit");
        if (options.Upsample) parameters.Add($"upsample={options.Ppi}");
        if (options.Baseline) parameters.Add("baseline");
        if (options.Output != OutputMode.Same) parameters.Add("output=" + options.Output.ToString().ToLowerInvariant());
        if (options.MissingPpi != MissingPpiPolicy.Assume72)
            parameters.Add("missingppi=" + options.MissingPpi.ToString().ToLowerInvariant());
        if (options.Quality != ProcessingOptions.Default.JpegQuality) parameters.Add($"quality={options.Quality}");
        parameters.AddRange(options.Extra);
        return parameters;
    }

    /// <summary>The reverse of <see cref="ToParameters"/>. An unknown or invalid parameter throws <see cref="ParameterException"/>.</summary>
    internal static FolderOptions FromParameters(IEnumerable<string> parameters)
    {
        var list = parameters.ToList();
        var parsed = ParameterParser.Parse(list);
        var extra = list.Where(p => p.TrimStart().StartsWith("filter", StringComparison.OrdinalIgnoreCase)).Select(p => p.Trim()).ToList();
        return new FolderOptions(
            parsed.Operations.HasFlag(Operations.EightBit),
            parsed.Operations.HasFlag(Operations.Upsample),
            (int)Math.Round(parsed.TargetPpi),
            parsed.Operations.HasFlag(Operations.Baseline),
            parsed.Output,
            parsed.MissingPpi,
            parsed.JpegQuality)
        {
            Extra = extra.Count == 0 ? [] : extra,
        };
    }

    /// <summary>A short message for the first problem found, or null when the folder can be saved.</summary>
    internal static string? Validate(WatchedFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.Name)) return "Enter a name.";
        if (string.IsNullOrWhiteSpace(folder.In)) return "Choose the In folder.";
        if (string.IsNullOrWhiteSpace(folder.Out)) return "Choose the Out folder.";
        if (string.IsNullOrWhiteSpace(folder.Error)) return "Choose the Error folder.";

        var paths = new List<(string Label, string Path)> { ("In", Normalize(folder.In)), ("Out", Normalize(folder.Out)), ("Error", Normalize(folder.Error)) };
        if (!string.IsNullOrWhiteSpace(folder.Originals)) paths.Add(("Originals", Normalize(folder.Originals)));
        for (var i = 0; i < paths.Count; i++)
        for (var j = i + 1; j < paths.Count; j++)
        {
            if (string.Equals(paths[i].Path, paths[j].Path, StringComparison.OrdinalIgnoreCase))
                return $"The {paths[i].Label} and {paths[j].Label} folders must be different.";
        }
        return null;
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
