using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;

namespace Illux.ImageTool.Pdf;

/// <summary>
/// Wraps one image in a one-page PDF 1.6 without changing it (spec 9): JPEG data is embedded byte for byte
/// (DCTDecode), other formats as lossless FlateDecode samples; an embedded ICC profile becomes an ICCBased colour
/// space, alpha becomes a soft mask. No colour conversion.
/// </summary>
public static class PdfImageWriter
{
    private const string PdfVersion = "1.6";

    // Uncompressed sample data handed to the deflater per call (whole rows).
    private const int StripBytes = 4 << 20;

    // Largest page side in default user space units that Acrobat accepts (PDF 1.6 implementation limit).
    private const double MaxPageUnits = 14400;

    /// <summary>
    /// Writes <paramref name="pdfPath"/>: one page of (pixels / ppi × 72) points showing the image of
    /// <paramref name="imagePath"/>. On any exception the partial PDF is deleted and the exception rethrown.
    /// </summary>
    public static void Write(string imagePath, Resolution resolution, string pdfPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentException.ThrowIfNullOrEmpty(pdfPath);
        if (!IsPositive(resolution.X) || !IsPositive(resolution.Y))
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Resolution must be positive.");

        var created = false;
        try
        {
            using var output = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
            created = true;
            var pdf = new PdfObjectWriter(output);
            pdf.WriteHeader(PdfVersion);
            var image = IsJpeg(imagePath) ? WriteJpegImage(pdf, imagePath) : WriteRasterImage(pdf, imagePath);
            WritePage(pdf, image, resolution);
        }
        catch
        {
            if (created) TryDelete(pdfPath);
            throw;
        }
    }

    private sealed record ImageObject(int Number, int Width, int Height);

    private static ImageObject WriteJpegImage(PdfObjectWriter pdf, string path)
    {
        var info = JpegInspector.Inspect(path);
        if (info.Precision != 8 || info.Coding is JpegCoding.Lossless or JpegCoding.Arithmetic)
            throw new NotSupportedException($"A {info.Precision}-bit {info.Coding} JPEG cannot be embedded with DCTDecode.");

        var colorSpace = ColorSpace(pdf, info.Components, info.IccProfile);
        // img2pdf's rule: a 4-component JPEG with an Adobe APP14 marker stores inverted CMYK (as Photoshop writes
        // it), so it gets /Decode [1 0 1 0 1 0 1 0]; without the marker the values are taken as they are.
        var decode = info.Components == 4 && info.HasAdobeMarker ? " /Decode [1 0 1 0 1 0 1 0]" : "";

        var number = pdf.Reserve();
        pdf.WriteStream(number,
            ImageEntries(info.Width, info.Height, colorSpace, 8) + " /Filter /DCTDecode" + decode,
            output =>
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                input.CopyTo(output);
            });
        return new ImageObject(number, info.Width, info.Height);
    }

    private static ImageObject WriteRasterImage(PdfObjectWriter pdf, string path)
    {
        MagickSetup.Configure();
        var settings = new MagickReadSettings();
        // Keep a Photoshop-layered TIFF a single image (the layers are not part of the flattened picture).
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", true);

        using var image = new MagickImage();
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // .NET IO: long/UNC paths
            image.Read(input, settings);

        // Palette images are expanded to their RGB values by the "RGB" export (lossless), not written as /Indexed.
        var (mapping, components) = image.ColorSpace switch
        {
            ImageMagick.ColorSpace.CMYK => ("CMYK", 4),
            ImageMagick.ColorSpace.Gray or ImageMagick.ColorSpace.LinearGray => ("I", 1),
            ImageMagick.ColorSpace.sRGB or ImageMagick.ColorSpace.RGB => ("RGB", 3),
            var other => throw new NotSupportedException($"Images in the {other} colour space cannot be written to PDF."),
        };
        var bits = image.Depth > 8 ? 16 : 8;
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);

        var colorSpace = ColorSpace(pdf, components, image.GetColorProfile()?.ToByteArray());

        var softMask = "";
        if (image.HasAlpha)
        {
            var mask = pdf.Reserve();
            pdf.WriteStream(mask, ImageEntries(width, height, "/DeviceGray", bits) + " /Filter /FlateDecode",
                output => WriteSamples(output, image, "A", bits));
            softMask = $" /SMask {mask} 0 R";
        }

        var number = pdf.Reserve();
        pdf.WriteStream(number, ImageEntries(width, height, colorSpace, bits) + " /Filter /FlateDecode" + softMask,
            output => WriteSamples(output, image, mapping, bits));
        return new ImageObject(number, width, height);
    }

    /// <summary>Exports the channels in <paramref name="mapping"/> row strip by row strip into a zlib stream (FlateDecode).</summary>
    private static void WriteSamples(Stream output, MagickImage image, string mapping, int bits)
    {
        var width = image.Width;
        var rowBytes = (long)width * mapping.Length * bits / 8;
        var rowsPerStrip = (uint)Math.Clamp(StripBytes / rowBytes, 1, image.Height);

        using var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var pixels = image.GetPixels();
        for (uint y = 0; y < image.Height; y += rowsPerStrip)
        {
            var rows = Math.Min(rowsPerStrip, image.Height - y);
            if (bits == 8)
            {
                zlib.Write(pixels.ToByteArray(0, (int)y, width, rows, mapping)
                    ?? throw new InvalidOperationException("Pixel export failed."));
            }
            else
            {
                var samples = pixels.ToShortArray(0, (int)y, width, rows, mapping)
                    ?? throw new InvalidOperationException("Pixel export failed.");
                var bytes = new byte[samples.Length * 2];
                for (var i = 0; i < samples.Length; i++)
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), samples[i]); // PDF samples are big-endian
                zlib.Write(bytes);
            }
        }
    }

    private static string ImageEntries(int width, int height, string colorSpace, int bits) =>
        $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent {bits}";

    /// <summary>
    /// The colour space for an image with <paramref name="components"/> colour components: ICCBased when the profile
    /// describes that many components (the ICC stream is written here), otherwise the matching device space.
    /// A profile for a different colour model is never embedded.
    /// </summary>
    private static string ColorSpace(PdfObjectWriter pdf, int components, byte[]? icc)
    {
        var (device, iccSpace) = components switch
        {
            1 => ("/DeviceGray", "GRAY"),
            3 => ("/DeviceRGB", "RGB "),
            4 => ("/DeviceCMYK", "CMYK"),
            _ => throw new NotSupportedException($"Images with {components} colour components cannot be written to PDF."),
        };

        // ICC header (ICC.1:2022, 7.2): 128 bytes, data colour space signature at bytes 16..19.
        if (icc is null || icc.Length < 128 || Encoding.ASCII.GetString(icc, 16, 4) != iccSpace) return device;

        var number = pdf.Reserve();
        pdf.WriteStream(number, $"/N {components} /Alternate {device}", output => output.Write(icc));
        return $"[/ICCBased {number} 0 R]";
    }

    private static void WritePage(PdfObjectWriter pdf, ImageObject image, Resolution resolution)
    {
        var widthPt = image.Width / resolution.X * 72;
        var heightPt = image.Height / resolution.Y * 72;
        // Ruling R19: Acrobat limits a page side to 14,400 units. For larger pages, PDF 1.6's /UserUnit makes one
        // unit N points (smallest integer N that fits) and the boxes and image matrix are divided by N, so the
        // physical size stays exact.
        var userUnit = (int)Math.Max(1, Math.Ceiling(Math.Max(widthPt, heightPt) / MaxPageUnits));
        var width = PdfObjectWriter.Number(widthPt / userUnit);
        var height = PdfObjectWriter.Number(heightPt / userUnit);
        var userUnitEntry = userUnit > 1 ? $" /UserUnit {userUnit}" : "";

        int catalog = pdf.Reserve(), pages = pdf.Reserve(), page = pdf.Reserve(), contents = pdf.Reserve(), info = pdf.Reserve();
        pdf.WriteStream(contents, "", output => output.Write(Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm /Im0 Do Q")));
        pdf.WriteObject(page,
            $"<< /Type /Page /Parent {pages} 0 R{userUnitEntry} /MediaBox [0 0 {width} {height}] /TrimBox [0 0 {width} {height}] " +
            $"/Resources << /XObject << /Im0 {image.Number} 0 R >> >> /Contents {contents} 0 R >>");
        pdf.WriteObject(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.WriteObject(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.WriteObject(info,
            $"<< /Producer {PdfObjectWriter.LiteralString("Illux Image Tool " + Version())} /CreationDate {PdfDate(DateTimeOffset.Now)} >>");
        pdf.WriteTrailer(catalog, info);
    }

    /// <summary>The product version without the source revision suffix that the SDK appends ("1.2.3+abc123").</summary>
    private static string Version()
    {
        var version = typeof(PdfImageWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(PdfImageWriter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>A PDF date string (ISO 32000-1, 7.9.4), e.g. <c>(D:20261023143000+02'00')</c>.</summary>
    private static string PdfDate(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"(D:{time:yyyyMMddHHmmss}{sign}{offset.Hours:D2}'{offset.Minutes:D2}')");
    }

    private static bool IsJpeg(string path)
    {
        Span<byte> magic = stackalloc byte[3];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return input.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == 3
            && magic[0] == 0xFF && magic[1] == 0xD8 && magic[2] == 0xFF;
    }

    private static bool IsPositive(double value) => value > 0 && double.IsFinite(value);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
