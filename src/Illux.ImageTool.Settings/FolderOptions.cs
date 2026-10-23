using Illux.ImageTool.Options;

namespace Illux.ImageTool.Settings;

/// <summary>The options of one watched folder as the edit dialog shows them.</summary>
internal sealed record FolderOptions(
    bool EightBit = true,
    bool Upsample = true,
    int Ppi = 200,
    bool Baseline = true,
    OutputMode Output = OutputMode.Same,
    MissingPpiPolicy MissingPpi = MissingPpiPolicy.Assume72,
    int Quality = 95)
{
    /// <summary>Parameters the dialog has no control for (<c>filter=...</c>); kept so editing a folder does not drop them.</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    // The synthesized equality would compare the Extra list by reference.
    public bool Equals(FolderOptions? other) =>
        other is not null
        && EightBit == other.EightBit && Upsample == other.Upsample && Ppi == other.Ppi && Baseline == other.Baseline
        && Output == other.Output && MissingPpi == other.MissingPpi && Quality == other.Quality
        && Extra.SequenceEqual(other.Extra);

    public override int GetHashCode() => HashCode.Combine(EightBit, Upsample, Ppi, Baseline, Output, MissingPpi, Quality);
}
