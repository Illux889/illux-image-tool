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
    private readonly ResolutionRule? _original;

    public RuleWindow() : this(null)
    {
    }

    public RuleWindow(ResolutionRule? rule)
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        _original = rule;
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
            WidthBox.Text = "40";
            HeightBox.Text = "30";
            LimitBox.Text = "40";
            AreaBox.Text = "1200";
            TargetBox.Text = "300";
            MinBox.Text = "250";
            MaxBox.Text = "350";
        }
        ShowSizeFields();
    }

    private void Load(ResolutionRule rule)
    {
        NameBox.Text = rule.Name;
        if (!_isAll) TestBox.SelectedIndex = Math.Max(Array.IndexOf(Tests, rule.Test), 0);
        WidthBox.Text = RulesEditor.Num(rule.WidthCm ?? 40);
        HeightBox.Text = RulesEditor.Num(rule.HeightCm ?? 30);
        LimitBox.Text = RulesEditor.Num(rule.LimitCm ?? 40);
        AreaBox.Text = RulesEditor.Num(rule.AreaCm2 ?? 1200);
        TargetBox.Text = rule.TargetPpi.ToString();
        MinBox.Text = rule.MinPpi.ToString();
        MaxBox.Text = rule.MaxPpi.ToString();
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
        var (rule, problems) = RulesEditor.Build(NameBox.Text, test, WidthBox.Text, HeightBox.Text, LimitBox.Text, AreaBox.Text,
            TargetBox.Text, MinBox.Text, MaxBox.Text, _original);
        if (rule is null)
        {
            await MessageWindow.ShowAsync(this, string.Join("\n", problems));
            return;
        }
        Close(rule);
    }
}
