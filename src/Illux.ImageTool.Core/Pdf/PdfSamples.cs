using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using Illux.ImageTool.Imaging;

namespace Illux.ImageTool.Pdf;

/// <summary>
/// Image pixels as PDF image samples (1 bit packed, 8 bit, or 16 bit big-endian), Flate-compressed; shared by
/// <see cref="PdfImageWriter"/> and the PDF input encoder.
/// </summary>
internal static class PdfSamples
{
    // Uncompressed sample data handed to the deflater per call (whole rows).
    private const int StripBytes = 4 << 20;

    /// <summary>
    /// The export mapping and PDF component count for an ImageMagick colour space that is a PDF device space (gray, RGB,
    /// CMYK), or null for any other (Lab, YCbCr, …), which would need a colour conversion.
    /// </summary>
    public static (string Mapping, int Components)? Layout(ImageMagick.ColorSpace colorSpace) => colorSpace switch
    {
        ImageMagick.ColorSpace.CMYK => ("CMYK", 4),
        ImageMagick.ColorSpace.Gray or ImageMagick.ColorSpace.LinearGray => ("I", 1),
        ImageMagick.ColorSpace.sRGB or ImageMagick.ColorSpace.RGB => ("RGB", 3),
        _ => null,
    };

    /// <summary><see cref="Layout"/>, or <see cref="NotSupportedException"/> for a colour space PDF cannot take as it is.</summary>
    public static (string Mapping, int Components) RequireLayout(MagickImage image) =>
        Layout(image.ColorSpace) ?? throw new NotSupportedException($"Images in the {image.ColorSpace} colour space cannot be written to PDF.");

    /// <summary>
    /// Reads an image file with .NET IO (long and UNC paths) after <see cref="MagickSetup.Configure"/>; a
    /// Photoshop-layered TIFF stays a single image (the layers are not part of the flattened picture).
    /// </summary>
    public static MagickImage Read(string path)
    {
        MagickSetup.Configure();
        var settings = new MagickReadSettings();
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", true);

        var image = new MagickImage();
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            image.Read(input, settings);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Exports the channels in <paramref name="mapping"/> row strip by row strip into a zlib stream (FlateDecode).
    /// <paramref name="bits"/> 1 (one channel only) packs each row MSB first, padded to whole bytes, with a sample set
    /// where the value is at least half.
    /// </summary>
    public static void WriteFlate(Stream output, MagickImage image, string mapping, int bits)
    {
        if (bits == 1 && mapping.Length != 1) throw new ArgumentException("1-bit samples need a single channel.", nameof(mapping));
        var width = image.Width;
        var rowBytes = ((long)width * mapping.Length * bits + 7) / 8;
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
            else if (bits == 1)
            {
                var gray = pixels.ToByteArray(0, (int)y, width, rows, mapping)
                    ?? throw new InvalidOperationException("Pixel export failed.");
                var packed = new byte[rowBytes * rows];
                for (var r = 0; r < rows; r++)
                    for (var x = 0; x < width; x++)
                        if (gray[r * width + x] >= 128) packed[r * rowBytes + x / 8] |= (byte)(0x80 >> (int)(x % 8));
                zlib.Write(packed);
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

    /// <summary><see cref="WriteFlate"/> into a new array.</summary>
    public static byte[] Flate(MagickImage image, string mapping, int bits)
    {
        using var output = new MemoryStream();
        WriteFlate(output, image, mapping, bits);
        return output.ToArray();
    }
}
