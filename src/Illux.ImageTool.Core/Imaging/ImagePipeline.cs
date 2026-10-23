using System.Globalization;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Imaging;

/// <param name="Messages">One line per action or warning, e.g. <c>16→8 bit</c>.</param>
/// <param name="FinalResolution">
/// The resolution the written file carries: the target when upsampled, otherwise the stored one. When the file
/// stores none and is not upsampled, this is 72 ppi (the value Photoshop assumes), because the PDF writer needs a
/// value. Results with <see cref="Status.Error"/> write nothing and carry 0×0.
/// </param>
/// <param name="Changed">False when the input was copied byte for byte (or nothing was written).</param>
public sealed record PipelineResult(Status Status, IReadOnlyList<string> Messages, string OutputPath, Resolution FinalResolution, bool Changed);

/// <summary>Converts one image: 16/32→8 bit, upsample to the target ppi, progressive→baseline JPEG (spec 8).</summary>
public static class ImagePipeline
{
    private const double AssumedPpi = 72;
    private const double LargeFactor = 4;

    // PNG pHYs stores whole pixels per metre, so e.g. 200 ppi reads back as 199.9996; don't resample for that.
    private const double PpiTolerance = 0.05;

    /// <summary>
    /// Writes exactly one image file at <paramref name="outputPath"/> in the input's format, or none when the
    /// result is an Error. Throws <see cref="UnsupportedImageException"/> for rejected inputs; on any exception no
    /// file is left at <paramref name="outputPath"/>.
    /// </summary>
    public static async Task<PipelineResult> RunAsync(string inputPath, string outputPath, ProcessingOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must differ from the input path.", nameof(outputPath));
        ct.ThrowIfCancellationRequested();

        MagickSetup.Configure();
        var source = SourceImage.Inspect(inputPath);
        var plan = Plan(source, options);
        if (plan.Error is { } error)
            return new PipelineResult(Status.Error, [error], outputPath, new Resolution(0, 0), false);
        ct.ThrowIfCancellationRequested();

        var messages = plan.Messages;
        var changed = true;
        try
        {
            if (!plan.To8Bit && plan.NewSize is null && !plan.ToBaseline)
            {
                File.Copy(inputPath, outputPath, overwrite: true);
                messages.Add("no change needed");
                changed = false;
            }
            else if (source.IsJpeg && !plan.To8Bit && plan.NewSize is null)
            {
                await JpegTran.ToBaselineAsync(inputPath, outputPath, ct).ConfigureAwait(false);
                messages.Add("progressive→baseline (lossless)");
            }
            else
            {
                var spec = new RewriteSpec(plan.To8Bit, plan.NewSize, options.TargetPpi, options.Filter, options.JpegQuality);
                var droppedLayers = await Task.Run(() => MagickRewriter.Rewrite(inputPath, outputPath, source, spec), ct)
                    .ConfigureAwait(false);
                if (source.IsJpeg) messages.Add(JpegReencodeMessage(source, options));
                if (droppedLayers) messages.Add("Photoshop layers removed (flattened image kept)");
            }
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }

        var status = plan.Warning ? Status.Warning : Status.Ok;
        return new PipelineResult(status, messages, outputPath, plan.FinalResolution, changed);
    }

    private sealed record PlanResult(
        bool To8Bit, (uint Width, uint Height)? NewSize, bool ToBaseline, Resolution FinalResolution,
        List<string> Messages, bool Warning, string? Error);

    private static PlanResult Plan(SourceImage source, ProcessingOptions options)
    {
        var messages = new List<string>();
        var warning = false;
        var target = options.TargetPpi;

        var to8Bit = options.Operations.HasFlag(Operations.EightBit) && source.Depth > 8;
        if (to8Bit) messages.Add($"{source.Depth}→8 bit");

        var resolution = source.Resolution;
        (uint Width, uint Height)? newSize = null;
        if (options.Operations.HasFlag(Operations.Upsample))
        {
            if (resolution is null)
            {
                switch (options.MissingPpi)
                {
                    case MissingPpiPolicy.Error:
                        return new PlanResult(false, null, false, new Resolution(0, 0), messages, false,
                            "no resolution stored (missingppi=error)");
                    case MissingPpiPolicy.Skip:
                        messages.Insert(0, "no resolution stored — upsampling skipped");
                        warning = true;
                        break;
                    default:
                        messages.Insert(0, $"no resolution stored — assumed {Format(AssumedPpi)} ppi");
                        warning = true;
                        resolution = new Resolution(AssumedPpi, AssumedPpi);
                        break;
                }
            }

            if (resolution is not null && (resolution.X < target - PpiTolerance || resolution.Y < target - PpiTolerance))
            {
                // Per axis, so both axes end at the target (spec 8.2: non-square resolutions resampled per axis).
                newSize = (Scale(source.Width, target / resolution.X), Scale(source.Height, target / resolution.Y));
                var from = resolution.X == resolution.Y ? Format(resolution.X) : $"{Format(resolution.X)}×{Format(resolution.Y)}";
                messages.Add($"{from}→{Format(target)} ppi ({source.Width}×{source.Height} → {newSize.Value.Width}×{newSize.Value.Height}, {options.Filter})");

                var factor = Math.Max(target / resolution.X, target / resolution.Y);
                if (factor > LargeFactor)
                {
                    messages.Add($"upscale factor {Format(factor)}× is above {Format(LargeFactor)}×");
                    warning = true;
                }
            }
        }

        var toBaseline = options.Operations.HasFlag(Operations.Baseline) && source.Jpeg?.Coding == JpegCoding.Progressive;
        var final = newSize is not null ? new Resolution(target, target) : resolution ?? new Resolution(AssumedPpi, AssumedPpi);
        return new PlanResult(to8Bit, newSize, toBaseline, final, messages, warning, null);
    }

    private static string JpegReencodeMessage(SourceImage source, ProcessingOptions options) =>
        source.Jpeg!.Coding == JpegCoding.Progressive
            ? $"progressive→baseline (re-encoded, q{options.JpegQuality})"
            : $"re-encoded (q{options.JpegQuality})";

    private static uint Scale(uint size, double factor) =>
        (uint)Math.Max(1, Math.Round(size * factor, MidpointRounding.AwayFromZero));

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
