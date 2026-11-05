using ImageMagick;
using Illux.ImageTool.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// An image taken out of a PDF: <paramref name="Path"/> is a <c>.jpg</c> (the DCT data byte for byte) or a lossless
/// <c>.tif</c> of the samples; <paramref name="SoftMaskPath"/> is the image's soft mask as a gray TIFF and
/// <paramref name="SoftMaskId"/> the soft mask's object, under which its replacement belongs (ruling Q3).
/// </summary>
public sealed record ExtractedImage(string Path, bool IsJpeg, string? SoftMaskPath, PdfObjectId? SoftMaskId);

/// <summary>
/// Writes an image XObject as a temporary image file the image pipeline can process. Colours are never converted:
/// the samples keep their colour space, ICC profile and decode array (the TIFF holds the raw sample values), except
/// that an Indexed image is expanded to its base colour space.
/// </summary>
public static class PdfImageExtractor
{
    /// <summary>
    /// Writes the image <paramref name="image"/> of <paramref name="doc"/> (and its soft mask) into
    /// <paramref name="tempDir"/>. Throws <see cref="UnsupportedImageException"/> (message without the page and image
    /// prefix) for images that cannot be processed safely: the mask cases before anything is written, data that cannot
    /// be decoded while writing (the caller discards its temp folder on any error).
    /// </summary>
    public static ExtractedImage Extract(PdfDocument doc, PdfObjectId image, bool needsScaling, string tempDir)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(tempDir);

        var source = PdfImageObject.Read(doc, image);
        if (source.Codec == PdfImageObject.Jpx && source.Get("SMaskInData") is NumericToken { Double: not 0 })
            throw new UnsupportedImageException("JPEG 2000 with its own mask is not supported");
        if (needsScaling && source.Get("Mask") is ArrayToken)
            throw new UnsupportedImageException("a colour-key mask on an image that needs scaling is not supported");
        if (needsScaling && source.IsImageMask)
            throw new UnsupportedImageException("a stencil mask that needs scaling is not supported");

        PdfImageObject? mask = null;
        PdfObjectId? maskId = null;
        if (source.Dictionary.Data.TryGetValue("SMask", out var smask) && smask is IndirectReferenceToken maskReference)
        {
            maskId = new PdfObjectId(checked((int)maskReference.Data.ObjectNumber), maskReference.Data.Generation);
            mask = PdfImageObject.Read(doc, maskId.Value);
            if (mask.Dictionary.Data.ContainsKey("Matte"))
                throw new UnsupportedImageException("a soft mask with /Matte is not supported");
        }

        MagickSetup.Configure();
        Directory.CreateDirectory(tempDir);
        var stem = System.IO.Path.Combine(tempDir, $"pdf-image-{image.Number}-{image.Generation}");

        var isJpeg = source.Codec == PdfImageObject.Dct;
        var path = stem + (isJpeg ? ".jpg" : ".tif");
        if (isJpeg)
        {
            var data = source.Decode(); // the stream's own bytes for plain DCTDecode: no copy
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            output.Write(data.Span);
        }
        else
        {
            WriteTiff(source, path, softMask: false);
        }

        string? maskPath = null;
        if (mask != null)
        {
            maskPath = stem + "-smask.tif";
            WriteTiff(mask, maskPath, softMask: true);
        }
        return new ExtractedImage(path, isJpeg, maskPath, maskId);
    }

    /// <summary>
    /// Writes the samples of <paramref name="image"/> as a Zip-compressed TIFF: gray, RGB or CMYK by component count,
    /// 1 bit (single-component 1-bit images that are not soft masks), 8 or 16 bit, with the ICC profile of an ICCBased
    /// space. A soft mask is always 8 or 16-bit gray.
    /// </summary>
    private static void WriteTiff(PdfImageObject image, string path, bool softMask)
    {
        var colorSpace = softMask ? new PdfColorSpace(1, null, null) : image.ColorSpace();
        var data = image.Decode();

        using var tiff = image.Codec is null
            ? FromSamples(image, colorSpace, data, softMask)
            : FromCodec(image, colorSpace, data, softMask);

        tiff.Settings.Compression = CompressionMethod.Zip;
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        tiff.Write(output, MagickFormat.Tiff);
    }

    /// <summary>Raw samples (after the filters) as an image; Indexed is expanded, 2 and 4 bit become 8 bit.</summary>
    private static MagickImage FromSamples(PdfImageObject image, PdfColorSpace colorSpace, Memory<byte> data, bool softMask)
    {
        int width = image.Width, height = image.Height, bits = image.Bits;
        if (bits is not (1 or 2 or 4 or 8 or 16) || (colorSpace.Palette != null && bits > 8))
            throw new InvalidDataException($"The image has an invalid /BitsPerComponent {bits}.");

        var components = colorSpace.Components;
        var rowBytes = ((long)width * components * bits + 7) / 8;
        if (data.Length < rowBytes * height)
            throw new InvalidDataException($"The image data is {data.Length} bytes; {rowBytes * height} are needed.");

        byte[]? icc = colorSpace.Icc;
        var bilevel = false;
        ReadOnlyMemory<byte> samples;
        int depth;
        if (colorSpace.Palette is { } palette)
        {
            samples = Expand(image, palette, data.Span, rowBytes);
            components = palette.Base.Components;
            icc = palette.Base.Icc;
            depth = 8;
        }
        else if (bits is 8 or 16)
        {
            samples = data[..checked((int)(rowBytes * height))];
            depth = bits;
        }
        else
        {
            // 1, 2 and 4 bit: each sample scaled to 0..255 (the decode array still maps the whole range).
            var max = (1 << bits) - 1;
            var unpacked = new byte[(long)width * components * height];
            var span = data.Span;
            for (var y = 0; y < height; y++)
            {
                var row = span.Slice(checked((int)(y * rowBytes)), (int)rowBytes);
                for (var i = 0; i < width * components; i++)
                    unpacked[(long)y * width * components + i] = (byte)(Sample(row, i, bits) * 255 / max);
            }
            samples = unpacked;
            depth = 8;
            bilevel = bits == 1 && components == 1 && !softMask;
        }

        var result = ReadRaw(samples, width, height, components, depth);
        if (bilevel)
        {
            result.Settings.ColorType = ColorType.Bilevel;
            result.Depth = 1;
        }
        if (icc != null) result.SetProfile(new ImageProfile("icc", icc));
        return result;
    }

    /// <summary>Indexed samples as 8-bit samples of the base colour space; a <c>/Decode</c> maps the indices first.</summary>
    private static byte[] Expand(PdfImageObject image, PdfPalette palette, ReadOnlySpan<byte> data, long rowBytes)
    {
        int width = image.Width, height = image.Height, bits = image.Bits, n = palette.Base.Components;
        var max = (1 << bits) - 1;
        // ISO 32000-1, 8.9.5.2: an index v becomes Dmin + v × (Dmax − Dmin) / (2^bits − 1), then rounded and clamped.
        var (dMin, dMax) = image.Get("Decode") is ArrayToken { Length: >= 2 } decode
                           && decode.Data[0] is NumericToken d0 && decode.Data[1] is NumericToken d1
            ? (d0.Double, d1.Double)
            : (0, max);
        var map = new byte[max + 1];
        for (var v = 0; v <= max; v++)
            map[v] = (byte)Math.Clamp((int)Math.Round(dMin + v * (dMax - dMin) / max, MidpointRounding.AwayFromZero), 0, palette.HighValue);

        var result = new byte[(long)width * height * n];
        for (var y = 0; y < height; y++)
        {
            var row = data.Slice(checked((int)(y * rowBytes)), (int)rowBytes);
            for (var x = 0; x < width; x++)
            {
                var offset = map[Sample(row, x, bits)] * n;
                var target = result.AsSpan(checked((int)(((long)y * width + x) * n)), n);
                // A lookup table that is too short gives black (0) for the missing entries, as readers do.
                for (var c = 0; c < n; c++) target[c] = offset + c < palette.Lookup.Length ? palette.Lookup[offset + c] : (byte)0;
            }
        }
        return result;
    }

    /// <summary>Sample <paramref name="index"/> of a row packed at <paramref name="bits"/> per sample (1, 2, 4 or 8).</summary>
    private static int Sample(ReadOnlySpan<byte> row, int index, int bits)
    {
        if (bits == 8) return row[index];
        var bit = index * bits;
        return (row[bit >> 3] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
    }

    /// <summary>Interleaved samples (8 bit, or 16 bit big-endian) as a gray, RGB or CMYK image.</summary>
    private static MagickImage ReadRaw(ReadOnlyMemory<byte> samples, int width, int height, int components, int depth)
    {
        var (format, colorType) = components switch
        {
            1 => (MagickFormat.Gray, ColorType.Grayscale),
            3 => (MagickFormat.Rgb, ColorType.TrueColor),
            4 => (MagickFormat.Cmyk, ColorType.ColorSeparation),
            _ => throw new UnsupportedImageException($"a colour space with {components} components is not supported"),
        };
        var settings = new MagickReadSettings
        {
            Format = format,
            Width = (uint)width,
            Height = (uint)height,
            Depth = (uint)depth,
            Endian = Endian.MSB,
        };
        var image = new MagickImage();
        try
        {
            image.Read(samples.Span, settings);
            // Never let the writer pick a colour model from the pixels (neutral RGB would become gray).
            image.Settings.ColorType = colorType;
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// JPX data (or a DCT soft mask) decoded by ImageMagick. JPX needs a <c>/ColorSpace</c> with as many components as
    /// the data, as the replacement stream will be Flate samples in that colour space.
    /// </summary>
    private static MagickImage FromCodec(PdfImageObject image, PdfColorSpace colorSpace, Memory<byte> data, bool softMask)
    {
        if (!softMask && (image.Codec != PdfImageObject.Jpx || image.Get("ColorSpace") is null || colorSpace.Palette != null))
            throw PdfImageObject.Unsupported(image.Codec!);

        var result = new MagickImage();
        try
        {
            try
            {
                result.Read(data.Span);
            }
            catch (MagickException ex)
            {
                throw new UnsupportedImageException($"{image.Codec} image data is not supported", ex);
            }
            var components = result.ColorSpace switch
            {
                ImageMagick.ColorSpace.Gray or ImageMagick.ColorSpace.LinearGray => 1,
                ImageMagick.ColorSpace.CMYK => 4,
                _ => 3,
            };
            if (components != colorSpace.Components) throw PdfImageObject.Unsupported(image.Codec!);

            result.RemoveProfile("icc");
            if (colorSpace.Icc != null) result.SetProfile(new ImageProfile("icc", colorSpace.Icc));
            result.Alpha(AlphaOption.Off);
            result.Depth = result.Depth > 8 ? 16u : 8u;
            result.Settings.ColorType = components switch
            {
                1 => ColorType.Grayscale,
                4 => ColorType.ColorSeparation,
                _ => ColorType.TrueColor,
            };
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
