using ImageMagick;
using Illux.ImageTool.Jpeg;

namespace Illux.ImageTool.Imaging;

/// <summary>What the pipeline needs to know about an input before deciding anything (read without decoding pixels).</summary>
/// <param name="Format">Format detected from the file content, also used to write the output.</param>
/// <param name="Depth">Bits per channel (32 for floating-point TIFF).</param>
/// <param name="Resolution">Stored resolution, null when the file has none.</param>
/// <param name="Jpeg">Header information for JPEG input, otherwise null.</param>
internal sealed record SourceImage(MagickFormat Format, uint Width, uint Height, uint Depth, Resolution? Resolution, JpegInfo? Jpeg)
{
    private static readonly HashSet<MagickFormat> Supported =
    [
        MagickFormat.Jpeg, MagickFormat.Jpg, MagickFormat.Png, MagickFormat.Tiff, MagickFormat.Tiff64,
        MagickFormat.Bmp, MagickFormat.Bmp3, MagickFormat.Gif, MagickFormat.WebP,
    ];

    // Most camera RAW formats (and DNG) are TIFF containers, so the content alone would pass as TIFF.
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".raf", ".rw2", ".rwl",
        ".pef", ".srw", ".dcr", ".kdc", ".mrw", ".mef", ".mos", ".erf", ".3fr", ".fff", ".iiq", ".x3f", ".raw",
    };

    public bool IsJpeg => Jpeg is not null;

    public bool IsTiff => Format is MagickFormat.Tiff or MagickFormat.Tiff64;

    /// <summary>Read settings that force the detected format and keep a layered TIFF a single image.</summary>
    public MagickReadSettings ReadSettings() => CreateReadSettings(Format);

    private static MagickReadSettings CreateReadSettings(MagickFormat format)
    {
        var settings = new MagickReadSettings { Format = format };
        // Photoshop stores layers of a TIFF in tag 37724 next to the flattened image; ImageMagick would otherwise
        // return each layer as an extra frame, which looks like a multi-page TIFF.
        if (format is MagickFormat.Tiff or MagickFormat.Tiff64) settings.SetDefine(MagickFormat.Tiff, "ignore-layers", true);
        return settings;
    }

    /// <summary>Ruling Q12: Illustrator files are refused, whether they hold PDF or PostScript.</summary>
    internal const string IllustratorMessage = "Illustrator files are not supported";

    /// <summary>True for the <c>.ai</c> extension (ruling Q12; the content check is in the PDF scanner).</summary>
    internal static bool IsIllustratorName(string path) =>
        string.Equals(Path.GetExtension(path), ".ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>How far into a file the <c>%PDF-</c> header may start (ISO 32000-1, annex H.3: readers accept it within 1024 bytes).</summary>
    private const int PdfHeaderWindow = 1024;

    // Image files whose metadata (XMP, comments) may contain "%PDF-": never PDFs (ruling Q12).
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8];
    private static ReadOnlySpan<byte> TiffIntelSignature => [(byte)'I', (byte)'I', (byte)'*', 0];
    private static ReadOnlySpan<byte> TiffMotorolaSignature => [(byte)'M', (byte)'M', 0, (byte)'*'];
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// True when <paramref name="path"/> is a PDF by content: <c>%PDF-</c> lies within the first 1024 bytes and the
    /// file does not start with a JPEG, TIFF or PNG signature (whose metadata may hold that text; ruling Q12). Such
    /// files go to the PDF pipeline; <see cref="Inspect"/> does not take them as images.
    /// </summary>
    public static bool IsPdf(string path)
    {
        if (Directory.Exists(path)) return false; // Inspect gives the reason
        var header = new byte[PdfHeaderWindow];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var bytes = header.AsSpan(0, read);
        if (bytes.StartsWith(JpegSignature) || bytes.StartsWith(TiffIntelSignature) || bytes.StartsWith(TiffMotorolaSignature)
            || bytes.StartsWith(PngSignature))
            return false;
        return bytes.IndexOf("%PDF-"u8) >= 0;
    }

    /// <summary>Inspects <paramref name="path"/>; throws <see cref="UnsupportedImageException"/> for inputs we reject.</summary>
    public static SourceImage Inspect(string path)
    {
        if (Directory.Exists(path)) throw new UnsupportedImageException("is a folder, not a file");
        if (RawExtensions.Contains(Path.GetExtension(path)))
            throw new UnsupportedImageException("camera RAW files are not supported");
        if (IsIllustratorName(path)) throw new UnsupportedImageException(IllustratorMessage);

        var format = DetectFormat(path);
        var jpeg = format is MagickFormat.Jpeg or MagickFormat.Jpg ? InspectJpeg(path) : null;

        using var frames = new MagickImageCollection();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            frames.Ping(stream, CreateReadSettings(format)); // .NET opens the file: long and UNC paths work
        if (frames.Count > 1)
        {
            throw new UnsupportedImageException(format switch
            {
                MagickFormat.Tiff or MagickFormat.Tiff64 => $"multi-page TIFF is not supported ({frames.Count} pages)",
                _ => $"animated {format.ToString().ToUpperInvariant()} is not supported ({frames.Count} frames)",
            });
        }

        var first = frames[0];
        return new SourceImage(format, first.Width, first.Height, first.Depth, ResolutionReader.Read(first), jpeg);
    }

    /// <summary>Detects the format from the first bytes (magic numbers), so no decoder or delegate is started for rejected files.</summary>
    private static MagickFormat DetectFormat(string path)
    {
        var header = new byte[4096];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        var format = read == 0 ? null : MagickFormatInfo.Create(header.AsSpan(0, read))?.Format;
        return format switch
        {
            null => throw new UnsupportedImageException("unrecognised file format"),
            MagickFormat.Psd or MagickFormat.Psb => throw new UnsupportedImageException("Photoshop documents (PSD/PSB) are not supported"),
            { } f when Supported.Contains(f) => f,
            { } f => throw new UnsupportedImageException($"{f.ToString().ToUpperInvariant()} files are not supported"),
        };
    }

    internal const string IncompleteJpegMessage =
        "the JPEG is incomplete (it ends before the image data does); the file is probably damaged or was cut off";

    private static JpegInfo InspectJpeg(string path)
    {
        JpegInfo info;
        try { info = JpegInspector.Inspect(path); }
        catch (InvalidDataException ex) { throw new UnsupportedImageException($"unreadable JPEG: {ex.Message}", ex); }

        if (info.Coding == JpegCoding.Lossless) throw new UnsupportedImageException("lossless JPEG is not supported");
        if (info.Coding == JpegCoding.Arithmetic) throw new UnsupportedImageException("arithmetic-coded JPEG is not supported");
        if (info.Precision != 8) throw new UnsupportedImageException($"{info.Precision}-bit JPEG is not supported");
        if (!JpegInspector.HasEndOfImage(path)) throw new UnsupportedImageException(IncompleteJpegMessage);
        return info;
    }
}
