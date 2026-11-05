using System.Globalization;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Imaging;

/// <param name="Messages">One line per action or warning, e.g. <c>16→8 bit</c>.</param>
/// <param name="FinalResolution">
/// The resolution the written file carries: per axis the rule's target where that axis was resampled, otherwise the
/// stored value. When the file stores none and is not resampled, this is 72 ppi (the value Photoshop assumes), because the
/// PDF writer needs a value. Formats that cannot store a resolution (GIF) are written without one, yet still report
/// the resolution here (ruling R15). Results with <see cref="Status.Error"/> write nothing and carry 0×0.
/// </param>
/// <param name="Changed">False when the input was copied byte for byte (or nothing was written).</param>
public sealed record PipelineResult(Status Status, IReadOnlyList<string> Messages, string OutputPath, Resolution FinalResolution, bool Changed);

/// <summary>
/// Converts one image: 16/32→8 bit, resample to the ppi of the matching resolution rule (up or down, design spec
/// 5.4), progressive→baseline JPEG (spec 8).
/// </summary>
public static class ImagePipeline
{
    private const double AssumedPpi = 72;
    private const double CmPerInch = 2.54;
    private const double LargeFactor = 4;

    /// <summary>Largest output side in pixels (JPEG's hard limit is 65,535); checked before resampling.</summary>
    internal const uint MaxDimension = 65000;

    // PNG pHYs stores whole pixels per metre, so e.g. 200 ppi reads back as 199.9996; don't resample for that.
    private const double PpiTolerance = 0.05;

    /// <summary>
    /// Writes exactly one image file at <paramref name="outputPath"/> in the input's format, or none when the
    /// result is an Error. Throws <see cref="UnsupportedImageException"/> for rejected inputs and
    /// <see cref="ImageTooLargeException"/> when resampling would exceed <see cref="MaxDimension"/>; on any exception no
    /// file is left at <paramref name="outputPath"/>.
    /// </summary>
    public static Task<PipelineResult> RunAsync(string inputPath, string outputPath, ProcessingOptions options, CancellationToken ct) =>
        RunCoreAsync(inputPath, outputPath, options, null, ct);

    /// <summary>
    /// As the public <c>RunAsync</c>, except that <paramref name="drawnResolution"/> (the ppi an image is drawn at in a
    /// PDF) replaces whatever the file stores. The <c>missingppi</c> policy and the "assumed 72" messages never apply.
    /// </summary>
    internal static Task<PipelineResult> RunAsync(string inputPath, string outputPath, ProcessingOptions options,
        Resolution drawnResolution, CancellationToken ct) =>
        RunCoreAsync(inputPath, outputPath, options, drawnResolution, ct);

    private static async Task<PipelineResult> RunCoreAsync(string inputPath, string outputPath, ProcessingOptions options,
        Resolution? drawnResolution, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must differ from the input path.", nameof(outputPath));
        ct.ThrowIfCancellationRequested();

        MagickSetup.Configure();
        var source = SourceImage.Inspect(inputPath);
        if (drawnResolution is { } drawn) source = source with { Resolution = drawn };
        var plan = Plan(source, options);
        if (plan.Error is { } error)
            return new PipelineResult(Status.Error, [error], outputPath, new Resolution(0, 0), false);
        ct.ThrowIfCancellationRequested();

        var messages = plan.Messages;
        var warning = plan.Warning;
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
                // R14: with the Baseline operation off, a re-encoded progressive JPEG stays progressive.
                var keepProgressive = source.Jpeg?.Coding == JpegCoding.Progressive && !options.Operations.HasFlag(Operations.Baseline);
                var spec = new RewriteSpec(plan.To8Bit, plan.NewSize, plan.FinalResolution, options.Filter, options.JpegQuality, keepProgressive);
                var droppedLayers = await Task.Run(() => MagickRewriter.Rewrite(inputPath, outputPath, source, spec), ct)
                    .ConfigureAwait(false);
                if (source.IsJpeg) messages.Add(JpegReencodeMessage(plan.ToBaseline, options));
                if (droppedLayers)
                {
                    // Not asked for, so a Warning (anti-goal "changing anything not asked").
                    messages.Add("Photoshop layers removed (flattened image kept)");
                    warning = true;
                }
            }
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }

        var status = warning ? Status.Warning : Status.Ok;
        return new PipelineResult(status, messages, outputPath, plan.FinalResolution, changed);
    }

    internal sealed record PlanResult(
        bool To8Bit, (uint Width, uint Height)? NewSize, bool ToBaseline, Resolution FinalResolution,
        List<string> Messages, bool Warning, string? Error);

    /// <summary>
    /// Decides what to do with <paramref name="source"/>. A source whose <c>Resolution</c> is set (stored, or the drawn
    /// ppi of an image in a PDF) never triggers the missing-ppi messages.
    /// </summary>
    internal static PlanResult Plan(SourceImage source, ProcessingOptions options)
    {
        var messages = new List<string>();
        var warning = false;

        var to8Bit = options.Operations.HasFlag(Operations.EightBit) && source.Depth > 8;
        if (to8Bit) messages.Add($"{source.Depth}→8 bit");

        var resolution = source.Resolution;
        (uint Width, uint Height)? newSize = null;
        if (options.Operations.HasFlag(Operations.Resample))
        {
            if (resolution is null)
            {
                switch (options.MissingPpi)
                {
                    case MissingPpiPolicy.Error:
                        return new PlanResult(false, null, false, new Resolution(0, 0), messages, false,
                            "no resolution stored (missingppi=error)");
                    case MissingPpiPolicy.Skip:
                        messages.Insert(0, "no resolution stored — resampling skipped");
                        warning = true;
                        break;
                    default:
                        messages.Insert(0, $"no resolution stored — assumed {Format(AssumedPpi)} ppi");
                        warning = true;
                        resolution = new Resolution(AssumedPpi, AssumedPpi);
                        break;
                }
            }

            if (resolution is { } stored)
            {
                // The print size at the current ppi picks the rule; resampling keeps it (spec 5.4).
                var widthCm = source.Width / stored.X * CmPerInch;
                var heightCm = source.Height / stored.Y * CmPerInch;
                var rule = ResolutionRules.Select(options.Rules, widthCm, heightCm);
                double target = rule.TargetPpi;
                var changeX = OutsideRange(stored.X, rule);
                var changeY = OutsideRange(stored.Y, rule);
                if (changeX || changeY)
                {
                    // Per axis (spec 8.2, ruling R13): only an axis outside the rule's range is resampled to the
                    // target; an axis inside it keeps its ppi and pixel count.
                    var factorX = changeX ? target / stored.X : 1;
                    var factorY = changeY ? target / stored.Y : 1;
                    var resampled = new Resolution(changeX ? target : stored.X, changeY ? target : stored.Y);
                    newSize = (Scale(source.Width, factorX), Scale(source.Height, factorY));
                    if (newSize.Value.Width > MaxDimension || newSize.Value.Height > MaxDimension)
                    {
                        throw new ImageTooLargeException(
                            $"resampling {Format(stored)}→{Format(resampled)} ppi would make the image " +
                            $"{newSize.Value.Width}×{newSize.Value.Height} px; the limit is {MaxDimension} px per side");
                    }
                    messages.Add(
                        $"{Format(stored)}→{Format(resampled)} ppi (rule \"{rule.Name}\": {Cm(widthCm)} × {Cm(heightCm)} cm; " +
                        $"{source.Width}×{source.Height} → {newSize.Value.Width}×{newSize.Value.Height}, " +
                        $"{options.Filter.ToString().ToLowerInvariant()})");
                    resolution = resampled;

                    var factor = Math.Max(factorX, factorY); // only enlargement warns; a reduction is never above 1
                    if (factor > LargeFactor)
                    {
                        messages.Add($"upscale factor {Format(factor)}× is above {Format(LargeFactor)}×");
                        warning = true;
                    }
                }
                else
                {
                    messages.Add($"ppi inside {rule.MinPpi}–{rule.MaxPpi} (rule \"{rule.Name}\") — not resampled");
                }
            }
        }

        if (resolution is null && options.Output is OutputMode.Pdf or OutputMode.Both)
        {
            // The PDF page size comes from the resolution; without one it would silently be 72 ppi (final review M1).
            messages.Add($"no resolution stored — PDF page size assumes {Format(AssumedPpi)} ppi");
            warning = true;
        }

        var toBaseline = options.Operations.HasFlag(Operations.Baseline) && source.Jpeg?.Coding == JpegCoding.Progressive;
        var final = resolution ?? new Resolution(AssumedPpi, AssumedPpi);
        return new PlanResult(to8Bit, newSize, toBaseline, final, messages, warning, null);
    }

    private static string JpegReencodeMessage(bool toBaseline, ProcessingOptions options) =>
        toBaseline
            ? $"progressive→baseline (re-encoded, q{options.JpegQuality})"
            : $"re-encoded (q{options.JpegQuality})";

    // PNG pHYs rounding (see PpiTolerance) must not push a stored value just outside the range.
    private static bool OutsideRange(double ppi, ResolutionRule rule) =>
        ppi < rule.MinPpi - PpiTolerance || ppi > rule.MaxPpi + PpiTolerance;

    private static string Cm(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    // One value when both axes print the same (a drawn resolution in a PDF is rarely exactly equal on both axes).
    private static string Format(Resolution r) => Format(r.X) is var x && Format(r.Y) is var y && x == y ? x : $"{x}×{y}";

    private static uint Scale(uint size, double factor) =>
        (uint)Math.Max(1, Math.Round(size * factor, MidpointRounding.AwayFromZero));

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
