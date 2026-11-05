using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Writer;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Turns a processed temporary image file back into a replacement image stream. The new dictionary is the old one
/// with only <c>/Width</c>, <c>/Height</c>, <c>/BitsPerComponent</c>, <c>/Filter</c>, <c>/DecodeParms</c> (removed) and
/// <c>/Length</c> changed, plus <c>/ColorSpace</c> (the base) and <c>/Decode</c> (removed: it mapped the indices) for
/// an expanded Indexed image. Colour space, ICC profile and decode array are otherwise kept: the samples are the same
/// raw values the extractor wrote.
/// </summary>
public static class PdfImageEncoder
{
    /// <summary>
    /// Encodes <paramref name="processedPath"/> as the new data of image <paramref name="image"/>: a JPEG as DCT data byte
    /// for byte, anything else as Flate samples. When <paramref name="extracted"/> has a soft mask, the mask is scaled by
    /// <paramref name="factors"/> (new size = old size × factor, rounded) with <paramref name="filter"/> and encoded as a
    /// Flate gray image for the mask's own object.
    /// </summary>
    public static (PdfImageStream Image, PdfImageStream? SoftMask) Encode(PdfDocument doc, PdfObjectId image,
        string processedPath, ExtractedImage extracted, (double X, double Y) factors, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(processedPath);
        ArgumentNullException.ThrowIfNull(extracted);
        if (!IsPositive(factors.X) || !IsPositive(factors.Y))
            throw new ArgumentOutOfRangeException(nameof(factors), factors, "Scale factors must be positive.");

        MagickSetup.Configure();
        var source = PdfImageObject.Read(doc, image);
        var colorSpace = source.ColorSpace();
        var palette = colorSpace.Palette;
        var components = palette?.Base.Components ?? colorSpace.Components;

        PdfImageStream result;
        if (PdfImageWriter.IsJpeg(processedPath))
        {
            var info = JpegInspector.Inspect(processedPath);
            CheckComponents(info.Components, components);
            var data = File.ReadAllBytes(processedPath);
            result = Stream(Replace(source.Dictionary, info.Width, info.Height, 8, PdfImageObject.Dct, data.Length), data);
        }
        else
        {
            using var processed = ReadImage(processedPath);
            CheckComponents(Components(processed), components);
            var bits = source.IsImageMask ? 1 : processed.Depth > 8 ? 16 : 8;
            var data = bits == 1 ? PackedFlate(processed) : PdfSamples.Flate(processed, PdfSamples.Mapping(components), bits);
            var dictionary = Replace(source.Dictionary, (int)processed.Width, (int)processed.Height, bits, "FlateDecode", data.Length);
            if (palette != null) dictionary = dictionary.With("ColorSpace", palette.BaseToken).Without("Decode");
            result = Stream(dictionary, data);
        }

        PdfImageStream? softMask = null;
        if (extracted.SoftMaskPath is { } maskPath && extracted.SoftMaskId is { } maskId)
        {
            var mask = PdfImageObject.Read(doc, maskId);
            using var maskImage = ReadImage(maskPath);
            var width = Math.Max(1, (uint)Math.Round(maskImage.Width * factors.X, MidpointRounding.AwayFromZero));
            var height = Math.Max(1, (uint)Math.Round(maskImage.Height * factors.Y, MidpointRounding.AwayFromZero));
            if (width != maskImage.Width || height != maskImage.Height)
                maskImage.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true }, MagickRewriter.ToFilterType(filter));
            var bits = maskImage.Depth > 8 ? 16 : 8;
            var data = PdfSamples.Flate(maskImage, PdfSamples.Mapping(1), bits);
            softMask = Stream(Replace(mask.Dictionary, (int)width, (int)height, bits, "FlateDecode", data.Length), data);
        }
        return (result, softMask);
    }

    private static DictionaryToken Replace(DictionaryToken dictionary, int width, int height, int bits, string filter, int length) =>
        dictionary
            .With("Width", new NumericToken(width))
            .With("Height", new NumericToken(height))
            .With("BitsPerComponent", new NumericToken(bits))
            .With("Filter", NameToken.Create(filter))
            .With("Length", new NumericToken(length))
            .Without("DecodeParms");

    /// <summary>The dictionary in PDF syntax (PdfPig's token writer, as the rewriter uses, decision P1).</summary>
    private static PdfImageStream Stream(DictionaryToken dictionary, byte[] data)
    {
        using var bytes = new MemoryStream();
        TokenWriter.Instance.WriteToken(dictionary, bytes);
        return new PdfImageStream(bytes.ToArray(), data);
    }

    /// <summary>A stencil mask's samples: 1 bit per pixel, rows padded to whole bytes, set where the gray is at least half.</summary>
    private static byte[] PackedFlate(MagickImage image)
    {
        int width = (int)image.Width, height = (int)image.Height, rowBytes = (width + 7) / 8;
        using var pixels = image.GetPixels();
        var gray = pixels.ToByteArray(PdfSamples.Mapping(1)) ?? throw new InvalidOperationException("Pixel export failed.");
        var packed = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (gray[y * width + x] >= 128) packed[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));

        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(packed);
        return output.ToArray();
    }

    private static MagickImage ReadImage(string path)
    {
        var image = new MagickImage();
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // .NET IO: long/UNC paths
            image.Read(input);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static int Components(MagickImage image) => image.ColorSpace switch
    {
        ImageMagick.ColorSpace.Gray or ImageMagick.ColorSpace.LinearGray => 1,
        ImageMagick.ColorSpace.CMYK => 4,
        _ => 3,
    };

    /// <summary>The processed file must still have the PDF colour space's component count (no colour conversion).</summary>
    private static void CheckComponents(int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"The processed image has {actual} colour components; its PDF colour space has {expected}.");
    }

    private static bool IsPositive(double value) => value > 0 && double.IsFinite(value);
}
