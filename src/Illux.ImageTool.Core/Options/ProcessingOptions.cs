namespace Illux.ImageTool.Options;

[Flags]
public enum Operations
{
    None = 0,
    EightBit = 1,
    Upsample = 2,
    Baseline = 4,
    All = EightBit | Upsample | Baseline,
}

public enum OutputMode { Same, Pdf, Both }

public enum MissingPpiPolicy { Assume72, Skip, Error }

public enum ResampleFilter { Lanczos, Mitchell, Bicubic }

public enum Status { Ok = 0, Warning = 1, Error = 2 }

public static class StatusExtensions
{
    public static Status Worst(this Status a, Status b) => (Status)Math.Max((int)a, (int)b);
}

public sealed record ProcessingOptions(
    Operations Operations,
    double TargetPpi,
    OutputMode Output,
    MissingPpiPolicy MissingPpi,
    int JpegQuality,
    ResampleFilter Filter)
{
    public static ProcessingOptions Default { get; } = new(
        Operations.All, 200, OutputMode.Same, MissingPpiPolicy.Assume72, 95, ResampleFilter.Lanczos);
}
