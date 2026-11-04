using System.Globalization;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The non-UI half of the "Resolution rules" section: an editable copy of the rule list. The one "All other images"
/// rule always stays last, cannot be removed or moved, and no other rule can become it. The checks themselves are
/// <see cref="ResolutionRules.Validate"/>.
/// </summary>
internal sealed class RulesEditor
{
    private readonly List<ResolutionRule> _rules;

    internal RulesEditor(IEnumerable<ResolutionRule> rules) => _rules = [.. rules];

    internal IReadOnlyList<ResolutionRule> Rules => _rules;

    /// <summary>Inserts a rule before "All other images". Another "All" rule is refused.</summary>
    internal bool Add(ResolutionRule rule)
    {
        if (rule.Test == SizeTest.All) return false;
        var at = _rules.FindIndex(r => r.Test == SizeTest.All);
        _rules.Insert(at < 0 ? _rules.Count : at, rule);
        return true;
    }

    /// <summary>Replaces a rule. "All" stays "All", and no other rule can turn into it.</summary>
    internal bool Replace(int index, ResolutionRule rule)
    {
        if (!InRange(index) || (_rules[index].Test == SizeTest.All) != (rule.Test == SizeTest.All)) return false;
        _rules[index] = rule;
        return true;
    }

    internal bool Remove(int index)
    {
        if (!InRange(index) || _rules[index].Test == SizeTest.All) return false;
        _rules.RemoveAt(index);
        return true;
    }

    /// <summary>The new index, or -1 when the rule cannot move up.</summary>
    internal int MoveUp(int index) => Move(index, -1);

    /// <summary>The new index, or -1 when the rule cannot move down (it may not pass "All other images").</summary>
    internal int MoveDown(int index) => Move(index, 1);

    internal void Reset()
    {
        _rules.Clear();
        _rules.AddRange(ResolutionRules.Defaults);
    }

    /// <summary>Plain-English problems with the whole list; empty means it can be saved.</summary>
    internal IReadOnlyList<string> Validate() => ResolutionRules.Validate(_rules);

    /// <summary>Problems with one rule on its own (for the rule dialog).</summary>
    internal static IReadOnlyList<string> ValidateRule(ResolutionRule rule)
    {
        var all = ResolutionRules.Defaults[1];
        return ResolutionRules.Validate(rule.Test == SizeTest.All ? [rule] : [rule, all]);
    }

    /// <summary>For example <c>Fits within 40 × 30 cm → 300 ppi (leave 250–350)</c>.</summary>
    internal static string Describe(ResolutionRule r) => $"{DescribeSize(r)} → {r.TargetPpi} ppi (leave {r.MinPpi}–{r.MaxPpi})";

    internal static string DescribeSize(ResolutionRule r) => r.Test switch
    {
        SizeTest.FitsWithin => $"Fits within {Num(r.WidthCm)} × {Num(r.HeightCm)} cm",
        SizeTest.LongestSide => $"Longest side up to {Num(r.LimitCm)} cm",
        SizeTest.ShortestSide => $"Shortest side up to {Num(r.LimitCm)} cm",
        SizeTest.Area => $"Area up to {Num(r.AreaCm2)} cm²",
        _ => "All other images",
    };

    private int Move(int index, int step)
    {
        var to = index + step;
        if (!InRange(index) || !InRange(to) || _rules[index].Test == SizeTest.All || _rules[to].Test == SizeTest.All) return -1;
        (_rules[index], _rules[to]) = (_rules[to], _rules[index]);
        return to;
    }

    private bool InRange(int index) => index >= 0 && index < _rules.Count;

    private static string Num(double? value) => (value ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One line of the rules table in <see cref="MainWindow"/>.</summary>
public sealed record RuleRow(string Name, string Size, string Target, string Leave)
{
    internal static RuleRow From(ResolutionRule r) =>
        new(r.Name, RulesEditor.DescribeSize(r), r.TargetPpi.ToString(CultureInfo.InvariantCulture), $"{r.MinPpi}–{r.MaxPpi}");
}
