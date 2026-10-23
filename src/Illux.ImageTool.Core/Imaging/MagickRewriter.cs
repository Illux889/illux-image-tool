using ImageMagick;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Imaging;

/// <summary>What <see cref="MagickRewriter"/> changes.</summary>
/// <param name="NewSize">Pixel size after resampling; null means no resampling.</param>
/// <param name="NewResolution">Resolution written with <paramref name="NewSize"/> (per axis, ruling R13).</param>
/// <param name="KeepProgressive">Write a progressive JPEG (input was progressive and the Baseline operation is off, R14).</param>
internal sealed record RewriteSpec(
    bool To8Bit, (uint Width, uint Height)? NewSize, Resolution NewResolution, ResampleFilter Filter, int JpegQuality,
    bool KeepProgressive);

/// <summary>Decodes, changes and re-encodes an image with ImageMagick, in the input's own format.</summary>
internal static class MagickRewriter
{
    // Photoshop's layer data in a TIFF (tag 37724, ImageSourceData), which ImageMagick reads as a profile.
    private const string PhotoshopLayersProfile = "tiff:37724";

    /// <summary>Writes the changed image to <paramref name="outputPath"/>; returns true if the input had Photoshop layers (the output is flattened).</summary>
    public static bool Rewrite(string inputPath, string outputPath, SourceImage source, RewriteSpec spec)
    {
        // Magick.NET never auto-orients on read; EXIF Orientation is written back unchanged.
        // Files are opened by .NET (not by ImageMagick's C file API) so long (> 260) and UNC paths work.
        using var image = new MagickImage();
        using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            image.Read(input, source.ReadSettings());
        var colorType = OutputColorType(image.ColorType);
        var compression = image.Compression;
        var samplingFactor = image.GetAttribute("jpeg:sampling-factor");

        if (spec.NewSize is { } size)
        {
            image.Resize(new MagickGeometry(size.Width, size.Height) { IgnoreAspectRatio = true }, ToFilterType(spec.Filter));
            // ImageMagick writes this density into JFIF / TIFF / PNG pHYs and, while writing, also syncs an existing
            // EXIF X/YResolution + ResolutionUnit and Photoshop ResolutionInfo (8BIM 0x03ED) to it, so every
            // resolution tag in the output agrees (covered by Pipeline_Upsample_RewritesExifAndPhotoshopResolution).
            image.Density = new Density(spec.NewResolution.X, spec.NewResolution.Y, DensityUnit.PixelsPerInch);
        }

        // Reduce depth after resampling so the filter works at full precision (spec 8.2: "resampling is done at the
        // image's working bit depth"). SetImageDepth rounds to nearest without dithering; a floating-point TIFF
        // becomes 8-bit unsigned integer.
        if (spec.To8Bit) image.Depth = 8;

        // ImageMagick reads the layers' raw data as a profile but does not write it back, so the output holds only
        // the flattened image (which is what we want: the old layers would no longer match). Report it.
        var droppedLayers = image.GetProfile(PhotoshopLayersProfile) is not null;

        // Never convert colour: without an explicit type ImageMagick's writers inspect the pixels and store neutral
        // RGB content as grayscale (JPEG 1 component, TIFF min-is-black, PNG colour type 0) while an RGB ICC
        // profile is still embedded.
        if (colorType != ColorType.Undefined) image.Settings.ColorType = colorType;

        // ImageMagick's TIFF writer does not reuse the compression it read, so keep it explicitly.
        if (source.IsTiff) image.Settings.Compression = compression;

        if (source.IsJpeg)
        {
            image.Quality = (uint)spec.JpegQuality;
            image.Settings.Interlace = spec.KeepProgressive ? Interlace.Jpeg : Interlace.NoInterlace;
            // Without this ImageMagick drops chroma subsampling for quality >= 90.
            if (!string.IsNullOrEmpty(samplingFactor))
                image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", samplingFactor);
        }

        if (source.Format == MagickFormat.Png)
        {
            if (PngColorType(colorType) is { } pngType) image.Settings.SetDefine(MagickFormat.Png, "color-type", pngType);
            // Otherwise a known sRGB profile is replaced by an sRGB chunk and the embedded ICC bytes are lost.
            image.Settings.SetDefine(MagickFormat.Png, "preserve-iCCP", true);
        }

        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            image.Write(output, source.Format);
        return droppedLayers;
    }

    /// <summary>
    /// The colour model to write: the one that was read, except that palette and bilevel images become
    /// true colour / grayscale, because resampling creates colours and grey levels a palette cannot hold.
    /// </summary>
    private static ColorType OutputColorType(ColorType read) => read switch
    {
        ColorType.Bilevel => ColorType.Grayscale,
        ColorType.Palette => ColorType.TrueColor,
        ColorType.PaletteAlpha or ColorType.PaletteBilevelAlpha => ColorType.TrueColorAlpha,
        ColorType.Optimize => ColorType.Undefined,
        _ => read,
    };

    private static string? PngColorType(ColorType type) => type switch
    {
        ColorType.Grayscale => "0",
        ColorType.TrueColor => "2",
        ColorType.GrayscaleAlpha => "4",
        ColorType.TrueColorAlpha => "6",
        _ => null,
    };

    /// <remarks>
    /// "Bicubic" maps to Catmull-Rom (Keys cubic, a = -0.5), the usual meaning of bicubic in image editors;
    /// ImageMagick's own "Cubic" filter is a blurring B-spline.
    /// </remarks>
    internal static FilterType ToFilterType(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Lanczos => FilterType.Lanczos,
        ResampleFilter.Mitchell => FilterType.Mitchell,
        ResampleFilter.Bicubic => FilterType.Catrom,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    };
}
