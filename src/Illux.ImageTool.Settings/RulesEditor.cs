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
        var all = new ResolutionRule("All other images", SizeTest.All, null, null, null, null, 200, 150, 250);
        return ResolutionRules.Validate(rule.Test == SizeTest.All ? [rule] : [rule, all]);
    }

    /// <summary>
    /// A rule from the dialog's text. Only the fields the test needs are read (the others are left null). Text that is
    /// not a number is refused with a message naming the field; a number out of range is not changed here, it is
    /// refused by <see cref="ValidateRule"/> with its normal message.
    /// Text equal to the <paramref name="original"/> rule's displayed number keeps that number as it is.
    /// </summary>
    internal static (ResolutionRule? Rule, IReadOnlyList<string> Problems) Build(
        string? name, SizeTest test, string? width, string? height, string? limit, string? area,
        string? target, string? min, string? max, ResolutionRule? original = null)
    {
        var problems = new List<string>();
        double? w = null, h = null, l = null, a = null;
        if (test == SizeTest.FitsWithin)
        {
            w = ReadSize(width, "Width", original?.WidthCm, problems);
            h = ReadSize(height, "Height", original?.HeightCm, problems);
        }
        else if (test is SizeTest.LongestSide or SizeTest.ShortestSide) l = ReadSize(limit, "Limit", original?.LimitCm, problems);
        else if (test == SizeTest.Area) a = ReadSize(area, "Area", original?.AreaCm2, problems);
        var t = ReadPpi(target, "Target ppi", problems);
        var lo = ReadPpi(min, "Leave alone from", problems);
        var hi = ReadPpi(max, "Leave alone up to", problems);
        if (problems.Count > 0) return (null, problems);

        var rule = Build(name, test, w, h, l, a, t, lo, hi);
        var invalid = ValidateRule(rule);
        return invalid.Count > 0 ? (null, invalid) : (rule, []);
    }

    /// <summary>A rule that carries only the size numbers its test uses.</summary>
    internal static ResolutionRule Build(string? name, SizeTest test, double? width, double? height, double? limit, double? area,
        int target, int min, int max) =>
        new((name ?? "").Trim(), test,
            test == SizeTest.FitsWithin ? width : null,
            test == SizeTest.FitsWithin ? height : null,
            test is SizeTest.LongestSide or SizeTest.ShortestSide ? limit : null,
            test == SizeTest.Area ? area : null,
            target, min, max);

    /// <summary>"40.5" and "40,5" both read 40.5. No thousands separators: "1.000" and "1,000.5" are refused.</summary>
    internal static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        var s = (text ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9]+([.,][0-9]+)?$")) return false;
        var at = s.IndexOfAny(['.', ',']);
        if (at > 0 && s.Length - at - 1 == 3 && s[..at] != "0") return false; // looks like 1.000 (a thousand)
        return double.TryParse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Whole numbers only, digits only.</summary>
    internal static bool TryParseWhole(string? text, out int value)
    {
        value = 0;
        var s = (text ?? "").Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9]{1,9}$")
            && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static double? ReadSize(string? text, string field, double? stored, List<string> problems)
    {
        // A stored value such as 12.125 is shown as it is; unchanged text keeps it instead of being parsed again.
        if (stored is { } s && text?.Trim() == Num(s)) return s;
        if (TryParseNumber(text, out var v)) return v;
        problems.Add($"{field}: enter a number such as 40 or 40.5 (no thousands separator; use at most 2 decimals, e.g. 12.25).");
        return null;
    }

    private static int ReadPpi(string? text, string field, List<string> problems)
    {
        if (TryParseWhole(text, out var v)) return v;
        problems.Add($"{field}: enter a whole number.");
        return 0;
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

    /// <summary>The one number format for the table and the dialog: a point, no thousands separator, whatever the culture.</summary>
    internal static string Num(double? value) => (value ?? 0).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>One line of the rules table in <see cref="MainWindow"/>.</summary>
public sealed record RuleRow(string Name, string Size, string Target, string Leave)
{
    internal static RuleRow From(ResolutionRule r) =>
        new(r.Name, RulesEditor.DescribeSize(r), r.TargetPpi.ToString(CultureInfo.InvariantCulture), $"{r.MinPpi}–{r.MaxPpi}");
}
