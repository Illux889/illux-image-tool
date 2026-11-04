using Illux.ImageTool.Options;
using Illux.ImageTool.Settings;

namespace Illux.ImageTool.Tests;

/// <summary>The pure logic behind the "Resolution rules" section of the settings window; the same on every OS.</summary>
public sealed class RulesEditorTests
{
    private static ResolutionRule Longest(string name, double cm) =>
        new(name, SizeTest.LongestSide, null, null, cm, null, 250, 200, 300);

    private static string[] Names(RulesEditor editor) => editor.Rules.Select(r => r.Name).ToArray();

    [Fact]
    public void Add_InsertsBeforeAll()
    {
        var editor = new RulesEditor(ResolutionRules.Defaults);

        Assert.True(editor.Add(Longest("Poster", 100)));

        Assert.Equal(["Small", "Poster", "Large"], Names(editor));
        Assert.Equal(SizeTest.All, editor.Rules[^1].Test);
    }

    [Fact]
    public void Add_AnotherAllRule_IsRefused()
    {
        var editor = new RulesEditor(ResolutionRules.Defaults);

        Assert.False(editor.Add(ResolutionRules.Defaults[1] with { Name = "Second" }));
        Assert.Equal(["Small", "Large"], Names(editor));
    }

    [Fact]
    public void MoveUp_Down_KeepAllLast()
    {
        var editor = new RulesEditor([Longest("A", 10), Longest("B", 20), Longest("C", 30), ResolutionRules.Defaults[1]]);

        Assert.Equal(0, editor.MoveUp(1));
        Assert.Equal(["B", "A", "C", "Large"], Names(editor));
        Assert.Equal(1, editor.MoveDown(0));
        Assert.Equal(["A", "B", "C", "Large"], Names(editor));

        Assert.Equal(-1, editor.MoveUp(0));      // already first
        Assert.Equal(-1, editor.MoveDown(2));    // may not pass "All"
        Assert.Equal(-1, editor.MoveUp(3));      // "All" does not move
        Assert.Equal(-1, editor.MoveDown(3));
        Assert.Equal(["A", "B", "C", "Large"], Names(editor));
    }

    [Fact]
    public void Remove_All_Refused()
    {
        var editor = new RulesEditor(ResolutionRules.Defaults);

        Assert.False(editor.Remove(1));
        Assert.Equal(["Small", "Large"], Names(editor));
        Assert.True(editor.Remove(0));
        Assert.Equal(["Large"], Names(editor));
        Assert.False(editor.Remove(5));
    }

    [Fact]
    public void Replace_KeepsAllAsAll()
    {
        var editor = new RulesEditor(ResolutionRules.Defaults);

        Assert.True(editor.Replace(1, ResolutionRules.Defaults[1] with { TargetPpi = 180 }));
        Assert.Equal(180, editor.Rules[1].TargetPpi);
        Assert.False(editor.Replace(1, Longest("X", 10)));            // "All" cannot become another test
        Assert.False(editor.Replace(0, ResolutionRules.Defaults[1])); // and no other rule can become "All"
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var editor = new RulesEditor([Longest("Only", 10), ResolutionRules.Defaults[1] with { TargetPpi = 100, MinPpi = 90, MaxPpi = 110 }]);

        editor.Reset();

        Assert.Equal(ResolutionRules.Defaults, editor.Rules);
    }

    [Fact]
    public void Save_Invalid_ReturnsMessages()
    {
        var editor = new RulesEditor(ResolutionRules.Defaults);
        editor.Add(Longest("Bad", 100) with { MinPpi = 400, TargetPpi = 300, MaxPpi = 350 });

        var problems = editor.Validate();

        Assert.Single(problems);
        Assert.Contains("Rule 'Bad'", problems[0]);
        Assert.Empty(new RulesEditor(ResolutionRules.Defaults).Validate());
    }

    [Fact]
    public void ValidateRule_ChecksOneRuleOnItsOwn()
    {
        Assert.Empty(RulesEditor.ValidateRule(Longest("Ok", 50)));
        Assert.Empty(RulesEditor.ValidateRule(ResolutionRules.Defaults[1]));
        Assert.NotEmpty(RulesEditor.ValidateRule(Longest("", 50)));
        Assert.NotEmpty(RulesEditor.ValidateRule(Longest("Huge", 5000)));
    }

    [Fact]
    public void Describe_Rule_PlainText()
    {
        Assert.Equal("Fits within 40 × 30 cm → 300 ppi (leave 250–350)", RulesEditor.Describe(ResolutionRules.Defaults[0]));
        Assert.Equal("All other images → 200 ppi (leave 150–250)", RulesEditor.Describe(ResolutionRules.Defaults[1]));
        Assert.Equal("Longest side up to 12.5 cm", RulesEditor.DescribeSize(Longest("A", 12.5)));
        Assert.Equal("Shortest side up to 20 cm", RulesEditor.DescribeSize(Longest("A", 20) with { Test = SizeTest.ShortestSide }));
        Assert.Equal("Area up to 900 cm²",
            RulesEditor.DescribeSize(new ResolutionRule("A", SizeTest.Area, null, null, null, 900, 100, 90, 110)));
    }
}
