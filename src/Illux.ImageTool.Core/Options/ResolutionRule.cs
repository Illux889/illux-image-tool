using System.Text.Json.Serialization;

namespace Illux.ImageTool.Options;

/// <summary>How a rule decides whether an image's print size belongs to it.</summary>
public enum SizeTest
{
    FitsWithin,
    LongestSide,
    ShortestSide,
    Area,
    All,
}

/// <summary>
/// One resolution rule: images whose print size passes <see cref="Test"/> are left alone when their ppi is within
/// <see cref="MinPpi"/>-<see cref="MaxPpi"/> and otherwise resampled to <see cref="TargetPpi"/>.
/// </summary>
/// <param name="WidthCm">For <see cref="SizeTest.FitsWithin"/> (compared long side to long side, so 30 x 40 equals 40 x 30).</param>
/// <param name="LimitCm">For <see cref="SizeTest.LongestSide"/> and <see cref="SizeTest.ShortestSide"/>.</param>
/// <param name="AreaCm2">For <see cref="SizeTest.Area"/>.</param>
public sealed record ResolutionRule(
    string Name,
    SizeTest Test,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? WidthCm,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? HeightCm,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LimitCm,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? AreaCm2,
    int TargetPpi,
    int MinPpi,
    int MaxPpi);
