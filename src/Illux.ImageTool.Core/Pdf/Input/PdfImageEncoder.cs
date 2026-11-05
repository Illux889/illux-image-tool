using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Turns a processed temporary image file back into a replacement image stream. The new dictionary is the old one
/// with only <c>/Width</c>, <c>/Height</c>, <c>/BitsPerComponent</c>, <c>/Filter</c>, <c>/DecodeParms</c> (removed) and
/// <c>/Length</c> changed, and where the old entries would now mean something else:
/// <list type="bullet">
/// <item>an expanded Indexed image gets its base as <c>/ColorSpace</c> and loses <c>/Decode</c> (it mapped the indices);</item>
/// <item>JPX data that becomes Flate loses <c>/Decode</c> (ignored for JPX, ISO 32000-1 7.4.9, unless an image mask);</item>
/// <item>a colour-key <c>/Mask</c> array is rescaled when the bit depth changes (ruling Q10).</item>
/// </list>
/// Colour space, ICC profile and decode array are otherwise kept: the samples are the same raw values the extractor wrote.
/// </summary>
public static class PdfImageEncoder
{
    /// <summary>
    /// Encodes <paramref name="processedPath"/> as the new data of image <paramref name="image"/>: a JPEG as DCT data byte
    /// for byte, anything else as Flate samples. When <paramref name="extracted"/> has a soft mask whose size changes,
    /// the mask is scaled by <paramref name="factors"/> (new size = old size × factor, rounded) with
    /// <paramref name="filter"/> and encoded as a Flate gray image for the mask's own object. With
    /// <paramref name="maskTo8Bit"/> (the 8-bit operation is on; ruling Q13) a mask deeper than 8 bits is also reduced to
    /// 8 bits, as its image is. A mask that keeps its size and depth gives null, so its stream stays as it is. Throws <see cref="NotSupportedException"/> when the new
    /// dictionary cannot be written exactly (<see cref="PdfSyntax"/>: e.g. a non-ASCII name kept from the old one).
    /// </summary>
    public static (PdfImageStream Image, PdfImageStream? SoftMask) Encode(PdfDocument doc, PdfObjectId image,
        string processedPath, ExtractedImage extracted, (double X, double Y) factors, ResampleFilter filter,
        bool maskTo8Bit = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(processedPath);
        ArgumentNullException.ThrowIfNull(extracted);
        if (!IsPositive(factors.X) || !IsPositive(factors.Y))
            throw new ArgumentOutOfRangeException(nameof(factors), factors, "Scale factors must be positive.");

        var source = PdfImageObject.Read(doc, image);
        var colorSpace = source.ColorSpace();
        var palette = colorSpace.Palette;
        var components = palette?.Base.Components ?? colorSpace.Components;

        DictionaryToken dictionary;
        byte[] data;
        int bits;
        if (PdfImageWriter.IsJpeg(processedPath))
        {
            if (palette != null) throw new InvalidOperationException("An expanded Indexed image cannot be stored as JPEG.");
            var info = JpegInspector.Inspect(processedPath);
            CheckComponents(info.Components, components);
            data = File.ReadAllBytes(processedPath);
            bits = 8;
            dictionary = Replace(source.Dictionary, info.Width, info.Height, bits, PdfImageObject.Dct, data.Length);
        }
        else
        {
            using var processed = PdfSamples.Read(processedPath);
            var (mapping, actual) = PdfSamples.RequireLayout(processed);
            CheckComponents(actual, components);
            bits = source.IsImageMask ? 1 : processed.Depth > 8 ? 16 : 8;
            data = PdfSamples.Flate(processed, mapping, bits);
            dictionary = Replace(source.Dictionary, (int)processed.Width, (int)processed.Height, bits, "FlateDecode", data.Length);
            if (palette != null) dictionary = dictionary.With("ColorSpace", palette.BaseToken).Without("Decode");
            if (source.Codec == PdfImageObject.Jpx && !source.IsImageMask) dictionary = dictionary.Without("Decode");
        }
        if (palette == null) dictionary = RescaleColourKey(source, dictionary, bits);
        var result = Stream(dictionary, data);

        PdfImageStream? softMask = null;
        if (extracted.SoftMaskPath is { } maskPath && extracted.SoftMaskId is { } maskId)
        {
            using var maskImage = PdfSamples.Read(maskPath);
            var width = Math.Max(1, (uint)Math.Round(maskImage.Width * factors.X, MidpointRounding.AwayFromZero));
            var height = Math.Max(1, (uint)Math.Round(maskImage.Height * factors.Y, MidpointRounding.AwayFromZero));
            var resize = width != maskImage.Width || height != maskImage.Height;
            var reduce = maskTo8Bit && maskImage.Depth > 8;
            if (resize || reduce)
            {
                if (resize) maskImage.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true }, MagickRewriter.ToFilterType(filter));
                // After resampling, as for images: the filter works at full precision.
                if (reduce) maskImage.Depth = 8;
                var mask = PdfImageObject.Read(doc, maskId);
                var maskBits = maskImage.Depth > 8 ? 16 : 8;
                var maskData = PdfSamples.Flate(maskImage, PdfSamples.RequireLayout(maskImage).Mapping, maskBits);
                softMask = Stream(Replace(mask.Dictionary, (int)width, (int)height, maskBits, "FlateDecode", maskData.Length), maskData);
            }
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

    /// <summary>
    /// A colour-key <c>/Mask</c> array holds sample values at the old bit depth; with a new depth each value v becomes
    /// round(v × (2^new − 1) / (2^old − 1)). That is exact for 1, 2 and 4 → 8 bit (× 255, 85, 17). For 16 → 8 bit it is
    /// round(v / 257), the rounding ImageMagick uses for the 8-bit conversion, and approximate at the range edges: an
    /// edge moves to its nearest 8-bit level, so pixels just outside a range that round onto that level become keyed
    /// too, and pixels just inside can drop out.
    /// </summary>
    private static DictionaryToken RescaleColourKey(PdfImageObject source, DictionaryToken dictionary, int bits)
    {
        if (source.Bits is 0 || source.Bits == bits || source.Get("Mask") is not ArrayToken key) return dictionary;
        double from = (1 << source.Bits) - 1, to = (1 << bits) - 1;
        var values = key.Data.Select(t => source.Resolve(t) is NumericToken n
                ? (IToken)new NumericToken((int)Math.Clamp(Math.Round(n.Double * to / from, MidpointRounding.AwayFromZero), 0, to))
                : throw new InvalidDataException("A colour-key /Mask holds a value that is not a number."))
            .ToList();
        return dictionary.With("Mask", new ArrayToken(values));
    }

    /// <summary>The dictionary in PDF syntax, written as the rewriter writes every object (<see cref="PdfSyntax"/>).</summary>
    private static PdfImageStream Stream(DictionaryToken dictionary, byte[] data) => new(PdfSyntax.ToBytes(dictionary), data);

    /// <summary>The processed file must still have the PDF colour space's component count (no colour conversion).</summary>
    private static void CheckComponents(int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"The processed image has {actual} colour components; its PDF colour space has {expected}.");
    }

    private static bool IsPositive(double value) => value > 0 && double.IsFinite(value);
}
