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

    /// <summary>Inspects <paramref name="path"/>; throws <see cref="UnsupportedImageException"/> for inputs we reject.</summary>
    public static SourceImage Inspect(string path)
    {
        if (RawExtensions.Contains(Path.GetExtension(path)))
            throw new UnsupportedImageException("camera RAW files are not supported");

        var format = DetectFormat(path);
        var jpeg = format is MagickFormat.Jpeg or MagickFormat.Jpg ? InspectJpeg(path) : null;

        using var frames = new MagickImageCollection();
        frames.Ping(path, CreateReadSettings(format));
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
            MagickFormat.Pdf or MagickFormat.Epdf or MagickFormat.Pdfa or MagickFormat.Ai =>
                throw new UnsupportedImageException("PDF input is not supported"),
            { } f when Supported.Contains(f) => f,
            { } f => throw new UnsupportedImageException($"{f.ToString().ToUpperInvariant()} files are not supported"),
        };
    }

    private static JpegInfo InspectJpeg(string path)
    {
        JpegInfo info;
        try { info = JpegInspector.Inspect(path); }
        catch (InvalidDataException ex) { throw new UnsupportedImageException($"unreadable JPEG: {ex.Message}", ex); }

        if (info.Coding == JpegCoding.Lossless) throw new UnsupportedImageException("lossless JPEG is not supported");
        if (info.Coding == JpegCoding.Arithmetic) throw new UnsupportedImageException("arithmetic-coded JPEG is not supported");
        if (info.Precision != 8) throw new UnsupportedImageException($"{info.Precision}-bit JPEG is not supported");
        return info;
    }
}
