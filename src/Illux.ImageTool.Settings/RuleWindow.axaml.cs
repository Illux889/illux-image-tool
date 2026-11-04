using Avalonia.Controls;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Settings;

/// <summary>
/// Add or edit one resolution rule. <see cref="Window.ShowDialog{TResult}(Window)"/> returns the rule, or null when
/// cancelled. Only the size fields the chosen test needs are shown. The "All other images" rule keeps its test.
/// </summary>
public partial class RuleWindow : Window
{
    // The order of the items in TestBox.
    private static readonly SizeTest[] Tests = [SizeTest.FitsWithin, SizeTest.LongestSide, SizeTest.ShortestSide, SizeTest.Area];

    private readonly bool _isAll;

    public RuleWindow() : this(null)
    {
    }

    public RuleWindow(ResolutionRule? rule)
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        _isAll = rule?.Test == SizeTest.All;
        Title = rule is null ? "Add rule" : "Edit rule";

        if (_isAll)
        {
            TestBox.Items.Clear();
            TestBox.Items.Add(new ComboBoxItem { Content = "All other images" });
            TestBox.SelectedIndex = 0;
            TestBox.IsEnabled = false;
        }
        TestBox.SelectionChanged += (_, _) => ShowSizeFields();
        OkButton.Click += async (_, _) => await TryAcceptAsync();
        CancelButton.Click += (_, _) => Close(null);

        if (rule is not null) Load(rule);
        else
        {
            NameBox.Text = "";
            TestBox.SelectedIndex = 0;
            WidthBox.Value = 40;
            HeightBox.Value = 30;
            LimitBox.Value = 40;
            AreaBox.Value = 1200;
        }
        ShowSizeFields();
    }

    private void Load(ResolutionRule rule)
    {
        NameBox.Text = rule.Name;
        if (!_isAll) TestBox.SelectedIndex = Math.Max(Array.IndexOf(Tests, rule.Test), 0);
        WidthBox.Value = (decimal?)rule.WidthCm ?? 40;
        HeightBox.Value = (decimal?)rule.HeightCm ?? 30;
        LimitBox.Value = (decimal?)rule.LimitCm ?? 40;
        AreaBox.Value = (decimal?)rule.AreaCm2 ?? 1200;
        TargetBox.Value = rule.TargetPpi;
        MinBox.Value = rule.MinPpi;
        MaxBox.Value = rule.MaxPpi;
    }

    private void ShowSizeFields()
    {
        var test = _isAll ? SizeTest.All : Tests[Math.Clamp(TestBox.SelectedIndex, 0, Tests.Length - 1)];
        var fits = test == SizeTest.FitsWithin;
        var side = test is SizeTest.LongestSide or SizeTest.ShortestSide;
        var area = test == SizeTest.Area;
        WidthLabel.IsVisible = WidthBox.IsVisible = HeightLabel.IsVisible = HeightBox.IsVisible = fits;
        LimitLabel.IsVisible = LimitBox.IsVisible = side;
        AreaLabel.IsVisible = AreaBox.IsVisible = area;
    }

    private async Task TryAcceptAsync()
    {
        var test = _isAll ? SizeTest.All : Tests[Math.Clamp(TestBox.SelectedIndex, 0, Tests.Length - 1)];
        var rule = new ResolutionRule(
            (NameBox.Text ?? "").Trim(), test,
            test == SizeTest.FitsWithin ? (double?)WidthBox.Value : null,
            test == SizeTest.FitsWithin ? (double?)HeightBox.Value : null,
            test is SizeTest.LongestSide or SizeTest.ShortestSide ? (double?)LimitBox.Value : null,
            test == SizeTest.Area ? (double?)AreaBox.Value : null,
            (int)(TargetBox.Value ?? 0), (int)(MinBox.Value ?? 0), (int)(MaxBox.Value ?? 0));

        var problems = RulesEditor.ValidateRule(rule);
        if (problems.Count > 0)
        {
            await MessageWindow.ShowAsync(this, string.Join("\n", problems));
            return;
        }
        Close(rule);
    }
}
