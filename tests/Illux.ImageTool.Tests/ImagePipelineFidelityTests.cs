using System.Buffers.Binary;
using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Pipeline output must keep the input's colour model, ICC profile and file paths intact
/// (review fix round 1: neutral RGB, PNG iCCP, long paths, size limit).
/// </summary>
public sealed class ImagePipelineFidelityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-fidelity-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] SrgbIcc = ColorProfiles.SRGB.ToByteArray();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string OutPath(string name)
    {
        var dir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static Task<PipelineResult> Run(string input, string output) =>
        ImagePipeline.RunAsync(input, output, ProcessingOptions.Default, CancellationToken.None);

    /// <summary>
    /// Writes an RGB file whose pixels are all neutral grey (R = G = B) with an embedded sRGB ICC profile,
    /// forcing a true RGB encoding (ImageMagick would otherwise store neutral content as grayscale).
    /// </summary>
    private string WriteNeutralRgb(string name, MagickFormat format, uint depth, double ppi)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        using var image = new MagickImage("gradient:gray20-gray90", new MagickReadSettings { Width = 64, Height = 48 });
        image.ColorSpace = ColorSpace.sRGB;
        image.SetProfile(ColorProfiles.SRGB);
        image.Density = new Density(ppi, ppi, DensityUnit.PixelsPerInch);
        image.Depth = depth;
        image.Settings.ColorType = ColorType.TrueColor;
        if (format == MagickFormat.Png)
        {
            image.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
            image.Settings.SetDefine(MagickFormat.Png, "preserve-iCCP", true);
        }
        image.Write(path, format);
        return path;
    }

    /// <summary>PNG IHDR colour type: 0 grey, 2 RGB, 3 palette, 4 grey+alpha, 6 RGBA.</summary>
    private static int PngColorType(string path) => File.ReadAllBytes(path)[25];

    private static string TiffPhotometric(string path)
    {
        using var image = new MagickImage(path);
        return image.GetAttribute("tiff:photometric") ?? "";
    }

    private static byte[]? Icc(string path)
    {
        using var image = new MagickImage(path);
        return image.GetProfile("icc")?.ToByteArray();
    }

    [Fact]
    public async Task Pipeline_NeutralRgbJpeg_StaysThreeComponents()
    {
        var input = WriteNeutralRgb("n.jpg", MagickFormat.Jpeg, 8, 100);
        Assert.Equal(3, JpegInspector.Inspect(input).Components);
        var output = OutPath("n.jpg");

        await Run(input, output);

        var info = JpegInspector.Inspect(output);
        Assert.Equal(3, info.Components);
        Assert.Equal(SrgbIcc, info.IccProfile);
    }

    [Theory]
    [InlineData(8u)]
    [InlineData(16u)]
    public async Task Pipeline_NeutralRgbTiff_StaysRgb(uint depth)
    {
        var input = WriteNeutralRgb($"n{depth}.tif", MagickFormat.Tiff, depth, 100);
        Assert.Equal("RGB", TiffPhotometric(input));
        var output = OutPath($"n{depth}.tif");

        await Run(input, output);

        Assert.Equal("RGB", TiffPhotometric(output));
        Assert.Equal(SrgbIcc, Icc(output));
    }

    [Fact]
    public async Task Pipeline_NeutralRgbPng_StaysRgb()
    {
        var input = WriteNeutralRgb("n.png", MagickFormat.Png, 16, 100);
        Assert.Equal(2, PngColorType(input));
        var output = OutPath("n.png");

        await Run(input, output);

        Assert.Equal(2, PngColorType(output));
        Assert.Equal(SrgbIcc, Icc(output));
    }

    [Fact]
    public async Task Pipeline_PalettePngUpsampled_BecomesTrueColour()
    {
        // Resampling creates new colours, so a palette image is written as true colour rather than re-quantised.
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "p.png");
        using (var image = new MagickImage("plasma:red-blue", new MagickReadSettings { Width = 40, Height = 30 }))
        {
            image.Quantize(new QuantizeSettings { Colors = 16 });
            image.Density = new Density(100, 100, DensityUnit.PixelsPerInch);
            image.Write(input, MagickFormat.Png8);
        }
        Assert.Equal(3, PngColorType(input));
        var output = OutPath("p.png");

        await Run(input, output);

        Assert.Equal(2, PngColorType(output));
        using var result = new MagickImage(output);
        Assert.Equal(80u, result.Width);
    }

    [Fact]
    public async Task Pipeline_Png16WithSrgbIcc_To8Bit_KeepsIccBytes()
    {
        var input = TestImages.Write(_dir, "s.png", 40, 30, ColorSpace.sRGB, 16, 300, icc: SrgbIcc);
        using (var check = new MagickImage(input))
        {
            // TestImages writes through ImageMagick, which swaps a known sRGB profile for an sRGB chunk; re-embed it.
            check.SetProfile(ColorProfiles.SRGB);
            check.Settings.SetDefine(MagickFormat.Png, "preserve-iCCP", true);
            check.Write(input);
        }
        Assert.Equal(SrgbIcc, Icc(input));
        var output = OutPath("s.png");

        var result = await Run(input, output);

        Assert.Equal(["16→8 bit"], result.Messages);
        Assert.Equal(SrgbIcc, Icc(output));
    }

    [Fact]
    public async Task Pipeline_PathsLongerThan260_Work()
    {
        var shortInput = TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 16, 100);
        var deep = _dir;
        while (deep.Length < 270) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        var input = Path.Combine(deep, "input image.png");
        var output = Path.Combine(deep, "output image.png");
        File.Copy(shortInput, input);
        Assert.True(output.Length > 260);

        var result = await Run(input, output);

        Assert.True(result.Changed);
        using var stream = File.OpenRead(output);
        using var image = new MagickImage(stream);
        Assert.Equal(80u, image.Width);
        Assert.Equal(8u, image.Depth);
    }

    [Fact]
    public async Task Pipeline_UpsampleBeyond65000Pixels_Throws()
    {
        var input = TestImages.Write(_dir, "tiny.jpg", 400, 400, ColorSpace.sRGB, 8, 1);
        Assert.Equal((1.0, 1.0, 1), JpegInspector.Inspect(input).JfifDensity);
        var output = OutPath("tiny.jpg");

        var ex = await Assert.ThrowsAsync<ImageTooLargeException>(() => Run(input, output));

        Assert.Contains("80000×80000", ex.Message);
        Assert.Contains("65000", ex.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PngColorTypeHelper_ReadsIhdr()
    {
        // Guard for the helper above: IHDR is the first chunk, its colour type byte sits at offset 25.
        var path = TestImages.Write(_dir, "g.png", 8, 8, ColorSpace.Gray, 8, 300);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("IHDR"u8.ToArray(), bytes[12..16]);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(0, PngColorType(path));
    }
}
