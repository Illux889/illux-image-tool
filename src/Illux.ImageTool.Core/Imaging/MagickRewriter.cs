using ImageMagick;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Imaging;

/// <summary>What <see cref="MagickRewriter"/> changes. <paramref name="NewSize"/> null means no resampling.</summary>
internal sealed record RewriteSpec(bool To8Bit, (uint Width, uint Height)? NewSize, double TargetPpi, ResampleFilter Filter, int JpegQuality);

/// <summary>Decodes, changes and re-encodes an image with ImageMagick, in the input's own format.</summary>
internal static class MagickRewriter
{
    // Photoshop's layer data in a TIFF (tag 37724, ImageSourceData), which ImageMagick reads as a profile.
    private const string PhotoshopLayersProfile = "tiff:37724";

    /// <summary>Writes the changed image to <paramref name="outputPath"/>; returns true if the input had Photoshop layers (the output is flattened).</summary>
    public static bool Rewrite(string inputPath, string outputPath, SourceImage source, RewriteSpec spec)
    {
        // Magick.NET never auto-orients on read; EXIF Orientation is written back unchanged.
        using var image = new MagickImage();
        image.Read(inputPath, source.ReadSettings());
        var compression = image.Compression;
        var samplingFactor = image.GetAttribute("jpeg:sampling-factor");

        if (spec.NewSize is { } size)
        {
            image.Resize(new MagickGeometry(size.Width, size.Height) { IgnoreAspectRatio = true }, ToFilterType(spec.Filter));
            // ImageMagick writes this density into JFIF / TIFF / PNG pHYs and, while writing, also syncs an existing
            // EXIF X/YResolution + ResolutionUnit and Photoshop ResolutionInfo (8BIM 0x03ED) to it, so every
            // resolution tag in the output agrees (covered by Pipeline_Upsample_RewritesExifAndPhotoshopResolution).
            image.Density = new Density(spec.TargetPpi, spec.TargetPpi, DensityUnit.PixelsPerInch);
        }

        // Reduce depth after resampling so the filter works at full precision (spec 8.2: "resampling is done at the
        // image's working bit depth"). SetImageDepth rounds to nearest without dithering; a floating-point TIFF
        // becomes 8-bit unsigned integer.
        if (spec.To8Bit) image.Depth = 8;

        // ImageMagick reads the layers' raw data as a profile but does not write it back, so the output holds only
        // the flattened image (which is what we want: the old layers would no longer match). Report it.
        var droppedLayers = image.GetProfile(PhotoshopLayersProfile) is not null;

        // ImageMagick's TIFF writer does not reuse the compression it read, so keep it explicitly.
        if (source.IsTiff) image.Settings.Compression = compression;

        if (source.IsJpeg)
        {
            image.Quality = (uint)spec.JpegQuality;
            image.Settings.Interlace = Interlace.NoInterlace; // baseline
            // Without this ImageMagick drops chroma subsampling for quality >= 90.
            if (!string.IsNullOrEmpty(samplingFactor))
                image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", samplingFactor);
        }

        image.Write(outputPath, source.Format);
        return droppedLayers;
    }

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
