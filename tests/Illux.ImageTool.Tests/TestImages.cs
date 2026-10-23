using ImageMagick;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Generates realistic test images (diagonal gradient plus noise) so JPEG compression and
/// resampling behave like they do on real photos. The format is chosen by the file extension of <c>name</c>.
/// </summary>
internal static class TestImages
{
    private const double Quantum = 65535; // Q16-HDRI quantum range

    public static byte[] SrgbIcc { get; } = ColorProfiles.SRGB.ToByteArray();

    public static byte[] CmykIcc { get; } = ColorProfiles.USWebCoatedSWOP.ToByteArray();

    /// <summary>
    /// Writes <paramref name="name"/> into <paramref name="dir"/> and returns its full path.
    /// <paramref name="ppi"/> null writes no density; <paramref name="icc"/> null writes no ICC profile.
    /// </summary>
    public static string Write(string dir, string name, int w, int h, ColorSpace cs, int depth, double? ppi,
        bool progressive = false, bool alpha = false, byte[]? icc = null, int quality = 92)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);

        var channels = alpha ? 4 : 3;
        var samples = new float[w * h * channels];
        var rnd = new Random(12345);
        var i = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var gx = (double)x / Math.Max(1, w - 1);
                var gy = (double)y / Math.Max(1, h - 1);
                samples[i++] = Noisy(gx, rnd);
                samples[i++] = Noisy(gy, rnd);
                samples[i++] = Noisy((gx + gy) / 2, rnd);
                if (alpha) samples[i++] = (float)(gx * Quantum);
            }
        }

        var mapping = alpha ? PixelMapping.RGBA : PixelMapping.RGB;
        using var image = new MagickImage();
        image.ReadPixels(samples, new PixelReadSettings((uint)w, (uint)h, StorageType.Quantum, mapping));
        image.ColorSpace = ColorSpace.sRGB;

        switch (cs)
        {
            case ColorSpace.sRGB:
                break;
            case ColorSpace.CMYK:
                image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
                break;
            case ColorSpace.Gray:
                image.ColorType = ColorType.Grayscale;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cs), cs, "Only sRGB, Gray and CMYK are supported.");
        }

        image.RemoveProfile("icc");
        if (icc != null) image.SetProfile(new ColorProfile(icc));

        image.Depth = (uint)depth;
        image.Quality = (uint)quality;
        if (progressive) image.Settings.Interlace = Interlace.Jpeg;

        // ppi == null: no density. (Density 0x0 with undefined units makes ImageMagick write JFIF units 0, 1:1.)
        image.Density = ppi is { } d
            ? new Density(d, d, DensityUnit.PixelsPerInch)
            : new Density(0.0, 0.0, DensityUnit.Undefined);

        image.Write(path);
        return path;
    }

    /// <summary>Value in 0..1 scaled to the Q16 quantum range (float samples, so 16-bit output has genuine 16-bit content) plus noise of about +-5 percent, clamped.</summary>
    private static float Noisy(double v, Random rnd) =>
        (float)(Math.Clamp(v + (rnd.NextDouble() - 0.5) * 0.1, 0, 1) * Quantum);
}
