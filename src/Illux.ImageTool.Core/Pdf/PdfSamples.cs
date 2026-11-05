using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;

namespace Illux.ImageTool.Pdf;

/// <summary>Image pixels as PDF image samples (8 bit, or 16 bit big-endian), Flate-compressed.</summary>
internal static class PdfSamples
{
    // Uncompressed sample data handed to the deflater per call (whole rows).
    private const int StripBytes = 4 << 20;

    /// <summary>The pixel export mapping for a PDF colour space with <paramref name="components"/> components.</summary>
    public static string Mapping(int components) => components switch
    {
        1 => "I",
        3 => "RGB",
        4 => "CMYK",
        _ => throw new NotSupportedException($"Images with {components} colour components cannot be written to PDF."),
    };

    /// <summary>Exports the channels in <paramref name="mapping"/> row strip by row strip into a zlib stream (FlateDecode).</summary>
    public static void WriteFlate(Stream output, MagickImage image, string mapping, int bits)
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

    /// <summary><see cref="WriteFlate"/> into a new array.</summary>
    public static byte[] Flate(MagickImage image, string mapping, int bits)
    {
        using var output = new MemoryStream();
        WriteFlate(output, image, mapping, bits);
        return output.ToArray();
    }
}
