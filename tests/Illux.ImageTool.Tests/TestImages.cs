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

    /// <summary>A minimal ICC v2 grayscale display profile (D50 white, gamma 2.2), as Magick.NET ships none.</summary>
    public static byte[] GrayIcc { get; } = BuildGrayIcc();

    private static byte[] BuildGrayIcc()
    {
        static byte[] Tag(string type, Action<BinaryWriterBe> body)
        {
            using var data = new MemoryStream();
            var w = new BinaryWriterBe(data);
            w.Ascii(type);
            w.U32(0);
            body(w);
            while (data.Length % 4 != 0) data.WriteByte(0);
            return data.ToArray();
        }

        const string text = "Illux test gray";
        var tags = new (string Signature, byte[] Data)[]
        {
            ("desc", Tag("desc", w =>
            {
                w.U32((uint)text.Length + 1);
                w.Ascii(text + "\0");
                w.U32(0); // Unicode language code
                w.U32(0); // Unicode count
                w.U16(0); // ScriptCode code
                w.U8(0); // ScriptCode count
                w.Zeros(67);
            })),
            ("cprt", Tag("text", w => w.Ascii("No copyright\0"))),
            ("wtpt", Tag("XYZ ", w => { w.U32(0x0000F6D6); w.U32(0x00010000); w.U32(0x0000D32D); })),
            ("kTRC", Tag("curv", w => { w.U32(1); w.U16(0x0233); })), // gamma 2.2 as u8Fixed8
        };

        var offset = 128 + 4 + 12 * tags.Length;
        var size = offset + tags.Sum(t => t.Data.Length);
        using var profile = new MemoryStream();
        var p = new BinaryWriterBe(profile);
        p.U32((uint)size);
        p.U32(0);
        p.U32(0x02100000);
        p.Ascii("mntr");
        p.Ascii("GRAY");
        p.Ascii("XYZ ");
        p.Zeros(12); // date
        p.Ascii("acsp");
        p.Zeros(28); // platform, flags, manufacturer, model, attributes, rendering intent
        p.U32(0x0000F6D6); // D50 illuminant
        p.U32(0x00010000);
        p.U32(0x0000D32D);
        p.Zeros(48); // creator, ID, reserved
        p.U32((uint)tags.Length);
        foreach (var (signature, data) in tags)
        {
            p.Ascii(signature);
            p.U32((uint)offset);
            p.U32((uint)data.Length);
            offset += data.Length;
        }
        foreach (var (_, data) in tags) profile.Write(data);
        return profile.ToArray();
    }

    private sealed class BinaryWriterBe(Stream output)
    {
        public void U8(byte v) => output.WriteByte(v);
        public void U16(ushort v) { U8((byte)(v >> 8)); U8((byte)v); }
        public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
        public void Ascii(string s) => output.Write(System.Text.Encoding.ASCII.GetBytes(s));
        public void Zeros(int n) => output.Write(new byte[n]);
    }

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
                image.ColorType = alpha ? ColorType.GrayscaleAlpha : ColorType.Grayscale;
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
