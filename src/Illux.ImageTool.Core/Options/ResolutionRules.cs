namespace Illux.ImageTool.Options;

public static class ResolutionRules
{
    public const int MaxAllowedPpi = 2400;
    private const double MinSizeCm = 0.1, MaxSizeCm = 1000, MinAreaCm2 = 0.01, MaxAreaCm2 = 1_000_000;

    // Print sizes are computed (pixels / ppi * 2.54), so an image of exactly 40 cm can come out as 40.0000000001.
    private const double Tolerance = 1e-6;

    public static IReadOnlyList<ResolutionRule> Defaults { get; } =
    [
        new("Small", SizeTest.FitsWithin, 40, 30, null, null, 300, 250, 350),
        new("Large", SizeTest.All, null, null, null, null, 200, 150, 250),
    ];

    /// <summary>True when an image printing <paramref name="widthCm"/> x <paramref name="heightCm"/> belongs to <paramref name="r"/>.</summary>
    public static bool Matches(ResolutionRule r, double widthCm, double heightCm)
    {
        var longSide = Math.Max(widthCm, heightCm);
        var shortSide = Math.Min(widthCm, heightCm);
        switch (r.Test)
        {
            case SizeTest.FitsWithin:
                if (r.WidthCm is not { } w || r.HeightCm is not { } h) return false;
                return longSide <= Math.Max(w, h) + Tolerance && shortSide <= Math.Min(w, h) + Tolerance;
            case SizeTest.LongestSide:
                return r.LimitCm is { } l1 && longSide <= l1 + Tolerance;
            case SizeTest.ShortestSide:
                return r.LimitCm is { } l2 && shortSide <= l2 + Tolerance;
            case SizeTest.Area:
                return r.AreaCm2 is { } a && widthCm * heightCm <= a + Tolerance;
            case SizeTest.All:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The first matching rule; the last rule when none matches (a valid list ends with <c>all</c>).</summary>
    public static ResolutionRule Select(IReadOnlyList<ResolutionRule> rules, double widthCm, double heightCm)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
            throw new ArgumentException("There must be at least one resolution rule to choose from.", nameof(rules));
        foreach (var r in rules)
            if (Matches(r, widthCm, heightCm)) return r;
        return rules[^1];
    }

    /// <summary>Plain-English problems with <paramref name="rules"/>; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<ResolutionRule> rules)
    {
        var problems = new List<string>();
        if (rules is null || rules.Count == 0)
        {
            problems.Add("There must be at least one rule, and the last one must apply to all sizes (test 'all').");
            return problems;
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (r is null)
            {
                // A "null" entry in config.json (final review M2): a problem to report, not a crash.
                problems.Add($"Rule {i + 1} is empty.");
                continue;
            }
            var label = string.IsNullOrWhiteSpace(r.Name) ? $"Rule {i + 1}" : $"Rule '{r.Name}'";
            if (string.IsNullOrWhiteSpace(r.Name)) problems.Add($"Rule {i + 1} needs a name.");

            if (r.MinPpi < 1 || r.MaxPpi > MaxAllowedPpi || r.MinPpi > r.TargetPpi || r.TargetPpi > r.MaxPpi)
                problems.Add($"{label}: ppi values must satisfy 1 <= minimum <= target <= maximum <= {MaxAllowedPpi}.");

            switch (r.Test)
            {
                case SizeTest.FitsWithin:
                    if (!InRange(r.WidthCm, MinSizeCm, MaxSizeCm) || !InRange(r.HeightCm, MinSizeCm, MaxSizeCm))
                        problems.Add($"{label}: width and height must be between {MinSizeCm} and {MaxSizeCm} cm.");
                    break;
                case SizeTest.LongestSide:
                case SizeTest.ShortestSide:
                    if (!InRange(r.LimitCm, MinSizeCm, MaxSizeCm))
                        problems.Add($"{label}: the side limit must be between {MinSizeCm} and {MaxSizeCm} cm.");
                    break;
                case SizeTest.Area:
                    if (!InRange(r.AreaCm2, MinAreaCm2, MaxAreaCm2))
                        problems.Add($"{label}: the area must be between {MinAreaCm2} and {MaxAreaCm2} cm2.");
                    break;
                case SizeTest.All:
                    break;
                default:
                    problems.Add($"{label}: unknown size test.");
                    break;
            }
        }

        var allCount = rules.Count(r => r?.Test == SizeTest.All);
        if (allCount != 1) problems.Add("Exactly one rule must apply to all sizes (test 'all').");
        else if (rules[^1]?.Test != SizeTest.All) problems.Add("The rule that applies to all sizes must be the last rule.");
        return problems;
    }

    private static bool InRange(double? value, double min, double max) =>
        value is { } v && !double.IsNaN(v) && v >= min && v <= max;
}
