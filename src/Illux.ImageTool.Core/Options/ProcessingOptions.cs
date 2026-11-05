namespace Illux.ImageTool.Options;

[Flags]
public enum Operations
{
    None = 0,
    EightBit = 1,
    /// <summary>Resample to the ppi of the matching resolution rule (AE parameter <c>resample</c>, alias <c>upsample</c>).</summary>
    Resample = 2,
    Baseline = 4,
    All = EightBit | Resample | Baseline,
}

public enum OutputMode { Same, Pdf, Both }

public enum MissingPpiPolicy { Assume72, Skip, Error }

public enum ResampleFilter { Lanczos, Mitchell, Bicubic }

public enum Status { Ok = 0, Warning = 1, Error = 2 }

public static class StatusExtensions
{
    public static Status Worst(this Status a, Status b) => (Status)Math.Max((int)a, (int)b);
}

/// <param name="Rules">
/// The resolution rules (Settings, <c>config.json</c>); never from an AE ticket. First match wins and the last rule
/// applies to all sizes (<see cref="ResolutionRules.Validate"/>).
/// </param>
public sealed record ProcessingOptions(
    Operations Operations,
    IReadOnlyList<ResolutionRule> Rules,
    OutputMode Output,
    MissingPpiPolicy MissingPpi,
    int JpegQuality,
    ResampleFilter Filter)
{
    public static ProcessingOptions Default { get; } = new(
        Operations.All, ResolutionRules.Defaults, OutputMode.Same, MissingPpiPolicy.Assume72, 95, ResampleFilter.Bicubic);
}
