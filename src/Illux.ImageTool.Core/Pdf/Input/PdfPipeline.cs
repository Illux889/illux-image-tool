using System.Runtime.InteropServices;
using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;
using UglyToad.PdfPig;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Processes the images inside a PDF in place (design spec 5): every image drawn in the PDF goes through the same
/// steps as an image file, with the ppi it is drawn at, and is written back into a PDF that is otherwise unchanged.
/// <list type="number">
/// <item>Scan (<see cref="PdfImageScanner"/>): the images, their largest drawn size and the inline images.</item>
/// <item>Plan each image with <see cref="ImagePipeline.Plan"/> from its stream dictionary and drawn resolution;
/// nothing is decoded (a JPEG's headers are read for progressive detection).</item>
/// <item>No image needs a change: the PDF is copied byte for byte (<c>no change needed</c>).</item>
/// <item>Otherwise, one image at a time: extract it into a temporary folder, run the image pipeline on it with the
/// drawn resolution, encode the result as the new image stream, and delete its temporary files before the next.</item>
/// <item>Rewrite the PDF with the new streams (<see cref="PdfRewriter"/>).</item>
/// </list>
/// Messages about an image start with <c>page N, image M: </c> (where it is first drawn). Any failure fails the whole
/// PDF: the exception's message is the user's message (<c>page N, image M: …</c> for a rejected image,
/// <c>the PDF could not be read (…)</c> for data that cannot be read or written back exactly).
/// </summary>
public static class PdfPipeline
{
    private const double CmPerInch = 2.54;

    /// <summary>
    /// Writes exactly one PDF at <paramref name="outputPdf"/>, or throws (<see cref="UnsupportedImageException"/>,
    /// <see cref="ImageTooLargeException"/>; out of memory and cancellation as they are) and leaves nothing new there.
    /// Temporary image files go in a folder next to <paramref name="outputPdf"/>, which is removed again.
    /// <see cref="PipelineResult.FinalResolution"/> is the lowest final ppi of any image (per axis; 0 × 0 without
    /// images), for logs only.
    /// </summary>
    public static Task<PipelineResult> RunAsync(string inputPdf, string outputPdf, ProcessingOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPdf);
        ArgumentException.ThrowIfNullOrEmpty(outputPdf);
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(Path.GetFullPath(inputPdf), Path.GetFullPath(outputPdf), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output path must differ from the input path.", nameof(outputPdf));
        ct.ThrowIfCancellationRequested();

        return Task.Run(() => RunCoreAsync(inputPdf, outputPdf, options, ct), ct);
    }

    /// <summary>One distinct image: where it is first drawn, its plan, and (once processed) what happened to it.</summary>
    private sealed class Job(PdfImageEntry entry, ImagePipeline.PlanResult plan, Resolution drawn, int width, int height)
    {
        public PdfImageEntry Entry { get; } = entry;
        public ImagePipeline.PlanResult Plan { get; } = plan;
        public Resolution Drawn { get; } = drawn;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public string Prefix => $"page {Entry.FirstPage}, image {Entry.FirstIndex}: ";
        public bool NeedsChange => Plan.To8Bit || Plan.NewSize is not null || Plan.ToBaseline;

        /// <summary>The image pipeline's result, when it ran.</summary>
        public PipelineResult? Result { get; set; }
    }

    private static async Task<PipelineResult> RunCoreAsync(string inputPdf, string outputPdf, ProcessingOptions options, CancellationToken ct)
    {
        var scan = PdfImageScanner.Scan(inputPdf);
        var messages = new List<string>();
        var warning = false;
        if (scan.InlineImages > 0)
        {
            messages.Add($"{scan.InlineImages} inline image(s) not processed");
            warning = true;
        }
        if (scan.Images.Count == 0)
        {
            messages.Add("no images found");
            File.Copy(inputPdf, outputPdf, overwrite: true);
            return new PipelineResult(Status.Warning, messages, outputPdf, new Resolution(0, 0), false);
        }

        // Every image is planned and checked before any is resampled, so a PDF that will fail fails early.
        List<Job> jobs;
        using (var pdf = OpenedPdf.Open(inputPdf))
            jobs = scan.Images.Select(entry => PlanImage(pdf.Document, entry, options)).ToList();
        ct.ThrowIfCancellationRequested();

        var changed = jobs.Any(j => j.NeedsChange);
        if (changed)
        {
            var replacements = await ProcessAsync(inputPdf, outputPdf, jobs, options, ct).ConfigureAwait(false);
            changed = replacements.Count > 0;
            ct.ThrowIfCancellationRequested();
            if (changed)
            {
                try
                {
                    PdfRewriter.Write(inputPdf, outputPdf, replacements);
                }
                catch (Exception ex) when (IsUnreadable(ex))
                {
                    throw Unreadable(ex);
                }
            }
        }
        if (!changed) File.Copy(inputPdf, outputPdf, overwrite: true);

        foreach (var job in jobs)
        {
            if (job.Entry.DifferentSizes)
            {
                messages.Add($"{job.Prefix}drawn at different sizes; processed for the largest ({Cm(job.Entry.WidthCm)} × {Cm(job.Entry.HeightCm)} cm)");
                warning = true;
            }
            if (job.Result is { } result)
            {
                messages.AddRange(result.Messages.Select(m => job.Prefix + m));
                warning |= result.Status == Status.Warning;
            }
            else
            {
                messages.AddRange(job.Plan.Messages.Select(m => job.Prefix + m));
                warning |= job.Plan.Warning;
                if (changed) messages.Add(job.Prefix + "no change needed");
            }
        }
        if (!changed) messages.Add("no change needed");

        var finals = jobs.Select(j => j.Result?.FinalResolution ?? j.Plan.FinalResolution).ToList();
        var lowest = new Resolution(finals.Min(r => r.X), finals.Min(r => r.Y));
        return new PipelineResult(warning ? Status.Warning : Status.Ok, messages, outputPdf, lowest, changed);
    }

    /// <summary>Plans one image from its dictionary and drawn size, as <see cref="ImagePipeline"/> would plan a file.</summary>
    private static Job PlanImage(PdfDocument document, PdfImageEntry entry, ProcessingOptions options)
    {
        var prefix = $"page {entry.FirstPage}, image {entry.FirstIndex}: ";
        return Guard(prefix, () =>
        {
            var image = PdfImageObject.Read(document, entry.Image);
            var jpeg = image.Codec == PdfImageObject.Dct ? InspectJpeg(image.Decode()) : null;
            var drawn = new Resolution(image.Width / (entry.WidthCm / CmPerInch), image.Height / (entry.HeightCm / CmPerInch));
            var source = new SourceImage(jpeg is null ? MagickFormat.Tiff : MagickFormat.Jpeg,
                (uint)image.Width, (uint)image.Height, (uint)image.Bits, drawn, jpeg);
            var plan = ImagePipeline.Plan(source, options);
            if (plan.Error is { } error) throw new UnsupportedImageException(error); // not expected: the resolution is set
            var job = new Job(entry, plan, drawn, image.Width, image.Height);

            // The image pipeline only takes 8-bit sequential or progressive Huffman JPEGs; others are left alone if they
            // need nothing.
            if (job.NeedsChange && jpeg is { } info && (info.Coding is JpegCoding.Lossless or JpegCoding.Arithmetic || info.Precision != 8))
                throw PdfImageObject.Unsupported(PdfImageObject.Dct);
            // The cheap rejections of the extractor (mask cases, colour space, filters), without decoding anything.
            if (job.NeedsChange) PdfImageExtractor.Check(document, entry.Image, plan.NewSize is not null);
            return job;
        });
    }

    /// <summary>
    /// Extracts, processes and encodes each image that needs a change, one at a time; returns the new streams by
    /// object (images, and soft masks whose size or depth changes).
    /// </summary>
    private static async Task<Dictionary<PdfObjectId, PdfImageStream>> ProcessAsync(
        string inputPdf, string outputPdf, List<Job> jobs, ProcessingOptions options, CancellationToken ct)
    {
        var replacements = new Dictionary<PdfObjectId, PdfImageStream>();
        var images = jobs.Select(j => j.Entry.Image).ToHashSet();
        var tempRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPdf))!, ".illux-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            foreach (var job in jobs.Where(j => j.NeedsChange))
            {
                ct.ThrowIfCancellationRequested();
                var id = job.Entry.Image;
                var dir = Path.Combine(tempRoot, $"{id.Number}-{id.Generation}");
                TestHook.Value?.Invoke("image", tempRoot);
                try
                {
                    // The document is open only while an image is extracted and encoded, not while it is resampled, so
                    // its data is not held next to the image pipeline's pixels, nor kept from one image to the next.
                    ExtractedImage extracted;
                    using (var pdf = OpenedPdf.Open(inputPdf))
                        extracted = Guard(job.Prefix, () => PdfImageExtractor.Extract(pdf.Document, id, job.Plan.NewSize is not null, dir));
                    Guard(job.Prefix, () => CheckSize(extracted.Path, job));

                    var processed = Path.Combine(dir, "processed" + Path.GetExtension(extracted.Path));
                    TestHook.Value?.Invoke("resample", inputPdf);
                    var result = await GuardAsync(job.Prefix,
                        () => ImagePipeline.RunAsync(extracted.Path, processed, options, job.Drawn, ct)).ConfigureAwait(false);
                    if (result.Status == Status.Error)
                        throw new UnsupportedImageException(job.Prefix + string.Join("; ", result.Messages)); // not expected
                    job.Result = result;
                    if (!result.Changed) continue;

                    var factors = job.Plan.NewSize is { } size
                        ? ((double)size.Width / job.Width, (double)size.Height / job.Height)
                        : (1.0, 1.0);
                    // Ruling Q13: the soft mask follows the image's 8-bit operation.
                    var maskTo8Bit = options.Operations.HasFlag(Operations.EightBit);
                    PdfImageStream image;
                    PdfImageStream? mask;
                    using (var pdf = OpenedPdf.Open(inputPdf))
                    {
                        (image, mask) = Guard(job.Prefix,
                            () => PdfImageEncoder.Encode(pdf.Document, id, processed, extracted, factors, options.Filter, maskTo8Bit));
                    }
                    replacements[id] = image;
                    // A soft mask shared by several images is scaled once (the first); it is mapped onto each image's
                    // unit square whatever its pixel size. An image's own new stream wins over its use as a mask.
                    if (mask is not null && extracted.SoftMaskId is { } maskId && !images.Contains(maskId))
                        replacements.TryAdd(maskId, mask);
                }
                finally
                {
                    TryDeleteFolder(dir);
                }
            }
        }
        finally
        {
            TryDeleteFolder(tempRoot);
        }
        return replacements;
    }

    /// <summary>The image dictionary's <c>/Width</c> and <c>/Height</c> drive the plan; the extracted data must agree.</summary>
    private static void CheckSize(string path, Job job)
    {
        int width, height;
        if (PdfImageWriter.IsJpeg(path))
        {
            var info = JpegInspector.Inspect(path);
            (width, height) = (info.Width, info.Height);
        }
        else
        {
            // Through .NET IO, so long and UNC paths work (the temp folder is deep).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = new MagickImageInfo(stream);
            (width, height) = ((int)info.Width, (int)info.Height);
        }
        if (width != job.Width || height != job.Height)
        {
            throw new InvalidDataException(
                $"the image data of {job.Prefix.TrimEnd(' ', ':')} is {width}×{height} px; its dictionary says {job.Width}×{job.Height}");
        }
    }

    private static JpegInfo InspectJpeg(Memory<byte> data)
    {
        using var stream = MemoryMarshal.TryGetArray<byte>(data, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);
        return JpegInspector.Inspect(stream);
    }

    /// <summary>
    /// Runs one step for an image and turns its failure into the user's message: a rejected image (or one too large)
    /// gets the <c>page N, image M: </c> prefix, data that cannot be read becomes <c>the PDF could not be read (…)</c>.
    /// </summary>
    private static T Guard<T>(string prefix, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex) when (Translate(prefix, ex) is { } translated)
        {
            throw translated;
        }
    }

    private static void Guard(string prefix, Action step) => Guard(prefix, () =>
    {
        step();
        return true;
    });

    private static async Task<T> GuardAsync<T>(string prefix, Func<Task<T>> step)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(prefix, ex) is { } translated)
        {
            throw translated;
        }
    }

    internal static Exception? Translate(string prefix, Exception ex) => ex switch
    {
        UnsupportedImageException => new UnsupportedImageException(prefix + PdfImageScanner.FirstLine(ex.Message), ex),
        ImageTooLargeException => new ImageTooLargeException(prefix + PdfImageScanner.FirstLine(ex.Message)),
        _ when IsUnreadable(ex) => Unreadable(ex),
        _ => null,
    };

    /// <summary>
    /// Failures reading or writing back the PDF's own data (InvalidDataException, NotSupportedException, decoder and
    /// parser errors, a file that ends too early). Not: cancellation, memory (reported by the caller as for image
    /// files) or other file-system errors.
    /// </summary>
    private static bool IsUnreadable(Exception ex) => ex is EndOfStreamException
        || ex is not (OperationCanceledException or OutOfMemoryException or MagickResourceLimitErrorException
            or IOException or UnauthorizedAccessException or UnsupportedImageException or ImageTooLargeException);

    private static UnsupportedImageException Unreadable(Exception ex) =>
        new($"the PDF could not be read ({PdfImageScanner.FirstLine(ex.Message)})", ex);

    /// <summary>
    /// Test hook: <c>("image", temporary root folder)</c> before each image is extracted, and
    /// <c>("resample", input PDF)</c> right before the image pipeline runs on it.
    /// </summary>
    internal static readonly AsyncLocal<Action<string, string>?> TestHook = new();

    /// <summary>The input PDF opened in PdfPig, with its file; failures to open read as <c>the PDF could not be read</c>.</summary>
    private sealed class OpenedPdf : IDisposable
    {
        private readonly FileStream _file;

        private OpenedPdf(FileStream file, PdfDocument document)
        {
            _file = file;
            Document = document;
        }

        public PdfDocument Document { get; }

        public static OpenedPdf Open(string path) => Guard("", () =>
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return new OpenedPdf(file, PdfDocument.Open(file, new ParsingOptions { SkipMissingFonts = true }));
            }
            catch
            {
                file.Dispose();
                throw;
            }
        });

        public void Dispose()
        {
            Document.Dispose();
            _file.Dispose();
        }
    }

    private static string Cm(double value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
