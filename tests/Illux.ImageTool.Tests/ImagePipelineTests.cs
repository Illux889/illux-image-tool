using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Tests;

public sealed class ImagePipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pipeline-" + Guid.NewGuid().ToString("N"));

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

    private static ProcessingOptions Options(Operations ops = Operations.All, MissingPpiPolicy missing = MissingPpiPolicy.Assume72) =>
        ProcessingOptions.Default with { Operations = ops, MissingPpi = missing };

    private static Task<PipelineResult> Run(string input, string output, ProcessingOptions? options = null) =>
        ImagePipeline.RunAsync(input, output, options ?? ProcessingOptions.Default, CancellationToken.None);

    private static double PpiX(IMagickImage<float> image) => image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X;

    private static double PpiY(IMagickImage<float> image) => image.Density.ChangeUnits(DensityUnit.PixelsPerInch).Y;

    private static void Rewrite(string path, Action<MagickImage> change)
    {
        using var image = new MagickImage(path);
        change(image);
        image.Write(path);
    }

    [Fact]
    public async Task Pipeline_Tiff16Rgb150_To8Bit200()
    {
        var input = TestImages.Write(_dir, "a.tif", 300, 200, ColorSpace.sRGB, 16, 150, icc: TestImages.SrgbIcc);
        Rewrite(input, i => i.Settings.Compression = CompressionMethod.LZW);
        var output = OutPath("a.tif");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(output, result.OutputPath);
        Assert.Equal(new Resolution(200, 200), result.FinalResolution);
        Assert.Contains("16→8 bit", result.Messages);
        Assert.Contains("150→200 ppi (300×200 → 400×267, Lanczos)", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(MagickFormat.Tiff, image.Format);
        Assert.Equal(8u, image.Depth);
        Assert.Equal(400u, image.Width);
        Assert.Equal(267u, image.Height);
        Assert.Equal(200, PpiX(image), 0.01);
        Assert.Equal(200, PpiY(image), 0.01);
        Assert.Equal(TestImages.SrgbIcc, image.GetProfile("icc")!.ToByteArray());
        Assert.Equal(CompressionMethod.LZW, image.Compression);
    }

    [Fact]
    public async Task Pipeline_Png16_To8Bit_SizeUnchangedAt300ppi()
    {
        var input = TestImages.Write(_dir, "a.png", 64, 48, ColorSpace.sRGB, 16, 300);
        var output = OutPath("a.png");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal(["16→8 bit"], result.Messages);
        Assert.Equal(300, result.FinalResolution.X, 0.1);
        using var image = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, image.Format);
        Assert.Equal(8u, image.Depth);
        Assert.Equal(64u, image.Width);
        Assert.Equal(48u, image.Height);
        Assert.Equal(300, PpiX(image), 0.1);
    }

    [Fact]
    public async Task Pipeline_Gray16AlphaPng_To8Bit_KeepsAlpha()
    {
        var input = TestImages.Write(_dir, "a.png", 64, 48, ColorSpace.Gray, 16, 300, alpha: true);
        var output = OutPath("a.png");

        await Run(input, output);

        using var image = new MagickImage(output);
        Assert.Equal(8u, image.Depth);
        Assert.True(image.HasAlpha);
        Assert.Equal(ColorSpace.Gray, image.ColorSpace);
    }

    [Fact]
    public async Task Pipeline_Cmyk16Tiff_StaysCmyk()
    {
        var input = TestImages.Write(_dir, "c.tif", 60, 40, ColorSpace.CMYK, 16, 150, icc: TestImages.CmykIcc);
        var output = OutPath("c.tif");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        using var image = new MagickImage(output);
        Assert.Equal(ColorSpace.CMYK, image.ColorSpace);
        Assert.Equal(8u, image.Depth);
        Assert.Equal(80u, image.Width);
        Assert.Equal(TestImages.CmykIcc, image.GetProfile("icc")!.ToByteArray());
    }

    [Fact]
    public async Task Pipeline_CmykJpegUpsampled_StaysCmyk()
    {
        var input = TestImages.Write(_dir, "c.jpg", 60, 40, ColorSpace.CMYK, 8, 100, icc: TestImages.CmykIcc);
        var output = OutPath("c.jpg");

        await Run(input, output);

        var info = JpegInspector.Inspect(output);
        Assert.Equal(4, info.Components);
        Assert.Equal(TestImages.CmykIcc, info.IccProfile);
        using var a = new MagickImage(input);
        using var b = new MagickImage(output);
        Assert.Equal(ColorSpace.CMYK, b.ColorSpace);
        Assert.Equal(120u, b.Width);
        // Same colours after enlargement: compare against the input enlarged the same way.
        a.Resize(new MagickGeometry(120, 80) { IgnoreAspectRatio = true }, FilterType.Lanczos);
        Assert.True(a.Compare(b, ErrorMetric.RootMeanSquared) < 0.02);
    }

    [Fact]
    public async Task Pipeline_ProgressiveJpegOnly_IsLossless()
    {
        var input = TestImages.Write(_dir, "p.jpg", 96, 64, ColorSpace.sRGB, 8, 300, progressive: true);
        var output = OutPath("p.jpg");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(["progressive→baseline (lossless)"], result.Messages);
        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        using var a = new MagickImage(input);
        using var b = new MagickImage(output);
        Assert.Equal(0.0, a.Compare(b, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Pipeline_ProgressiveJpeg100ppi_ReencodedBaseline200()
    {
        var input = TestImages.Write(_dir, "p.jpg", 90, 60, ColorSpace.sRGB, 8, 100, progressive: true);
        var output = OutPath("p.jpg");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains(result.Messages, m => m.Contains("re-encoded, q95"));
        Assert.Contains("100→200 ppi (90×60 → 180×120, Lanczos)", result.Messages);
        var info = JpegInspector.Inspect(output);
        Assert.Equal(JpegCoding.Baseline, info.Coding);
        Assert.Equal((200.0, 200.0, 1), info.JfifDensity);
        using var image = new MagickImage(output);
        Assert.Equal(180u, image.Width);
        Assert.Equal(120u, image.Height);
    }

    [Fact]
    public async Task Pipeline_ProgressiveJpeg_UpsampleWithBaselineOff_StaysProgressive()
    {
        // R14: re-encoding keeps the input's coding when the Baseline operation is off.
        var input = TestImages.Write(_dir, "p.jpg", 90, 60, ColorSpace.sRGB, 8, 100, progressive: true);
        var output = OutPath("p.jpg");

        var result = await Run(input, output, Options(Operations.EightBit | Operations.Upsample));

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains("re-encoded (q95)", result.Messages);
        Assert.DoesNotContain(result.Messages, m => m.Contains("baseline"));
        var info = JpegInspector.Inspect(output);
        Assert.Equal(JpegCoding.Progressive, info.Coding);
        Assert.Equal((200.0, 200.0, 1), info.JfifDensity);
        Assert.Equal(180, info.Width);
    }

    [Fact]
    public async Task Pipeline_ReencodedJpeg_KeepsSamplingFactorAndUsesQuality()
    {
        // quality 75 makes ImageMagick subsample chroma 2x2; at q >= 90 it would default to 1x1 unless told otherwise.
        var input = TestImages.Write(_dir, "s.jpg", 64, 64, ColorSpace.sRGB, 8, 100, quality: 75);
        var output = OutPath("s.jpg");
        using (var original = new MagickImage(input))
            Assert.Equal("2x2,1x1,1x1", original.GetAttribute("jpeg:sampling-factor"));

        var result = await Run(input, output);

        Assert.Contains("re-encoded (q95)", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal("2x2,1x1,1x1", image.GetAttribute("jpeg:sampling-factor"));
        Assert.Equal(95u, image.Quality);
    }

    [Fact]
    public async Task Pipeline_Already8Bit300ppiBaseline_CopiedByteForByte()
    {
        var input = TestImages.Write(_dir, "b.jpg", 64, 48, ColorSpace.sRGB, 8, 300);
        var output = OutPath("b.jpg");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(["no change needed"], result.Messages);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Pipeline_Above200_NotDownsampled()
    {
        var input = TestImages.Write(_dir, "a.tif", 60, 40, ColorSpace.sRGB, 16, 300);
        var output = OutPath("a.tif");

        var result = await Run(input, output);

        Assert.Equal(["16→8 bit"], result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(60u, image.Width);
        Assert.Equal(40u, image.Height);
        Assert.Equal(300, PpiX(image), 0.01);
    }

    [Fact]
    public async Task Pipeline_Png200ppi_NotResampledForPhysRounding()
    {
        // pHYs holds whole pixels per metre: 200 ppi is stored as 7874 px/m and reads back as 199.9996 ppi.
        var input = TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 8, 200);
        var output = OutPath("a.png");

        var result = await Run(input, output);

        Assert.False(result.Changed);
        Assert.Equal(["no change needed"], result.Messages);
    }

    [Fact]
    public async Task Pipeline_LayeredTiff_IsOneImage_StaleLayersDropped()
    {
        // ImageMagick can write Photoshop layer data itself: first image = composite, the rest = layers.
        var input = TestImages.Write(_dir, "l.tif", 60, 40, ColorSpace.sRGB, 16, 300);
        using (var layered = new MagickImageCollection())
        {
            layered.Add(new MagickImage(input));
            layered.Add(new MagickImage(input));
            layered[0].Settings.SetDefine(MagickFormat.Tiff, "write-layers", true);
            layered.Write(input, MagickFormat.Tiff);
        }
        using (var check = new MagickImage(input))
            Assert.NotNull(check.GetProfile("tiff:37724"));
        var output = OutPath("l.tif");
        using (var plain = new MagickImageCollection())
        {
            plain.Ping(input);
            Assert.Equal(2, plain.Count); // without care, the layer looks like a second page
        }

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains("Photoshop layers removed (flattened image kept)", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(8u, image.Depth);
        Assert.Null(image.GetProfile("tiff:37724"));
    }

    [Fact]
    public async Task Pipeline_OnlyBaselineOperation_DoesNotUpsample()
    {
        var input = TestImages.Write(_dir, "p.jpg", 64, 48, ColorSpace.sRGB, 8, 100, progressive: true);
        var output = OutPath("p.jpg");

        var result = await Run(input, output, Options(Operations.Baseline));

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal(["progressive→baseline (lossless)"], result.Messages);
        Assert.Equal(new Resolution(100, 100), result.FinalResolution);
        var info = JpegInspector.Inspect(output);
        Assert.Equal(JpegCoding.Baseline, info.Coding);
        Assert.Equal((100.0, 100.0, 1), info.JfifDensity);
        Assert.Equal(64, info.Width);
    }

    [Fact]
    public async Task Pipeline_MissingPpi_Assume72_Warning()
    {
        var input = TestImages.Write(_dir, "n.png", 72, 36, ColorSpace.sRGB, 8, null);
        var output = OutPath("n.png");

        var result = await Run(input, output);

        Assert.Equal(Status.Warning, result.Status);
        Assert.Contains("no resolution stored — assumed 72 ppi", result.Messages);
        Assert.Contains("72→200 ppi (72×36 → 200×100, Lanczos)", result.Messages);
        Assert.Equal(new Resolution(200, 200), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(200u, image.Width);
        Assert.Equal(100u, image.Height);
        Assert.Equal(200, PpiX(image), 0.1);
    }

    [Fact]
    public async Task Pipeline_MissingPpi_Skip_Warning()
    {
        var input = TestImages.Write(_dir, "n.png", 72, 36, ColorSpace.sRGB, 8, null);
        var output = OutPath("n.png");

        var result = await Run(input, output, Options(missing: MissingPpiPolicy.Skip));

        Assert.Equal(Status.Warning, result.Status);
        Assert.Contains("no resolution stored — upsampling skipped", result.Messages);
        Assert.Equal(new Resolution(72, 72), result.FinalResolution);
        using var a = new MagickImage(input);
        using var b = new MagickImage(output);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        Assert.Equal(0.0, a.Compare(b, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Pipeline_MissingPpi_Error()
    {
        var input = TestImages.Write(_dir, "n.png", 72, 36, ColorSpace.sRGB, 8, null);
        var output = OutPath("n.png");

        var result = await Run(input, output, Options(missing: MissingPpiPolicy.Error));

        Assert.Equal(Status.Error, result.Status);
        Assert.False(result.Changed);
        Assert.Contains(result.Messages, m => m.Contains("no resolution stored"));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Pipeline_MissingPpi_UpsampleOff_NoWarning()
    {
        var input = TestImages.Write(_dir, "n.png", 40, 30, ColorSpace.sRGB, 16, null);
        var output = OutPath("n.png");

        var result = await Run(input, output, Options(Operations.EightBit | Operations.Baseline, MissingPpiPolicy.Error));

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal(["16→8 bit"], result.Messages);
        Assert.Equal(new Resolution(72, 72), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(40u, image.Width);
    }

    [Fact]
    public async Task Pipeline_Factor5_Warning()
    {
        var input = TestImages.Write(_dir, "f.png", 20, 10, ColorSpace.sRGB, 8, 40);
        var output = OutPath("f.png");

        var result = await Run(input, output);

        Assert.Equal(Status.Warning, result.Status);
        Assert.Contains("upscale factor 5× is above 4×", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(100u, image.Width);
        Assert.Equal(50u, image.Height);
    }

    [Fact]
    public async Task Pipeline_NonSquare_OnlyLowAxisRaised()
    {
        // R13: an axis already at or above the target keeps its ppi and pixel count (never downsampled).
        var input = TestImages.Write(_dir, "n.tif", 150, 300, ColorSpace.sRGB, 8, null);
        Rewrite(input, i => i.Density = new Density(150, 300, DensityUnit.PixelsPerInch));
        var output = OutPath("n.tif");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains("150×300→200×300 ppi (150×300 → 200×300, Lanczos)", result.Messages);
        Assert.Equal(new Resolution(200, 300), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(200u, image.Width);
        Assert.Equal(300u, image.Height);
        Assert.Equal(200, PpiX(image), 0.01);
        Assert.Equal(300, PpiY(image), 0.01);
    }

    [Fact]
    public async Task Pipeline_NonSquare_BothAxesLow_EachRaisedToTarget()
    {
        var input = TestImages.Write(_dir, "n.tif", 100, 150, ColorSpace.sRGB, 8, null);
        Rewrite(input, i => i.Density = new Density(100, 150, DensityUnit.PixelsPerInch));
        var output = OutPath("n.tif");

        var result = await Run(input, output);

        Assert.Contains("100×150→200 ppi (100×150 → 200×200, Lanczos)", result.Messages);
        Assert.Equal(new Resolution(200, 200), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(200u, image.Width);
        Assert.Equal(200u, image.Height);
        Assert.Equal(200, PpiY(image), 0.01);
    }

    [Fact]
    public async Task Pipeline_KeepsExifOrientation_NoAutoRotate()
    {
        var input = TestImages.Write(_dir, "o.jpg", 120, 80, ColorSpace.sRGB, 8, 100);
        Rewrite(input, i =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            i.SetProfile(exif);
            i.Orientation = OrientationType.RightTop;
        });
        using (var check = new MagickImage(input))
            Assert.Equal((ushort)6, check.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
        var output = OutPath("o.jpg");

        await Run(input, output);

        using var image = new MagickImage(output);
        Assert.Equal(240u, image.Width);
        Assert.Equal(160u, image.Height);
        Assert.Equal((ushort)6, image.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public async Task Pipeline_Upsample_RewritesExifAndPhotoshopResolution()
    {
        var input = TestImages.Write(_dir, "r.jpg", 50, 40, ColorSpace.sRGB, 8, 100);
        JpegSegments.Insert(input, JpegSegments.Exif(100), JpegSegments.PhotoshopResolution(100));
        var output = OutPath("r.jpg");

        await Run(input, output);

        Assert.Equal((200.0, 200.0, 1), JpegInspector.Inspect(output).JfifDensity);
        using var image = new MagickImage(output);
        Assert.Equal(100u, image.Width);
        var exif = image.GetExifProfile()!;
        Assert.Equal(200, exif.GetValue(ExifTag.XResolution)!.Value.ToDouble(), 0.01);
        Assert.Equal(200, exif.GetValue(ExifTag.YResolution)!.Value.ToDouble(), 0.01);
        Assert.Equal((ushort)2, exif.GetValue(ExifTag.ResolutionUnit)!.Value);
        var photoshop = ResolutionReader.From8Bim(image.GetProfile("8bim")!.ToByteArray());
        Assert.Equal(new Resolution(200, 200), photoshop);
    }

    [Fact]
    public async Task Pipeline_Float32Tiff_To8Bit()
    {
        var input = TestImages.Write(_dir, "f.tif", 60, 40, ColorSpace.sRGB, 16, 150);
        Rewrite(input, i =>
        {
            i.Settings.SetDefine("quantum:format", "floating-point");
            i.Depth = 32;
        });
        using (var check = new MagickImage(input))
        {
            Assert.Equal(32u, check.Depth);
            Assert.Equal("floating-point", check.GetAttribute("quantum:format"));
        }
        var output = OutPath("f.tif");

        var result = await Run(input, output);

        Assert.Contains("32→8 bit", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(8u, image.Depth);
        Assert.NotEqual("floating-point", image.GetAttribute("quantum:format"));
        Assert.Equal(80u, image.Width);
    }

    [Fact]
    public async Task Pipeline_MultiPageTiff_Unsupported()
    {
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "m.tif");
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.Red, 10, 10));
            pages.Add(new MagickImage(MagickColors.Blue, 10, 10));
            pages.Write(input);
        }
        await AssertUnsupported(input, "m.tif", "multi-page");
    }

    [Fact]
    public async Task Pipeline_AnimatedGif_Unsupported()
    {
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "a.gif");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 10, 10));
            frames.Add(new MagickImage(MagickColors.Blue, 10, 10));
            frames.Write(input);
        }
        await AssertUnsupported(input, "a.gif", "animated");
    }

    [Fact]
    public async Task Pipeline_Psd_Unsupported()
    {
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "x.psd");
        using (var image = new MagickImage(MagickColors.Red, 10, 10)) image.Write(input);
        await AssertUnsupported(input, "x.psd", "PSD");
    }

    [Fact]
    public async Task Pipeline_Pdf_Unsupported()
    {
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "x.pdf");
        await File.WriteAllTextAsync(input, "%PDF-1.6\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");
        await AssertUnsupported(input, "x.pdf", "PDF");
    }

    [Fact]
    public async Task Pipeline_CameraRawExtension_Unsupported()
    {
        // DNG and most camera RAW files are TIFF-structured, so content sniffing alone would call them TIFF.
        var input = TestImages.Write(_dir, "x.tif", 10, 10, ColorSpace.sRGB, 8, 300);
        var raw = Path.ChangeExtension(input, ".dng");
        File.Move(input, raw);
        await AssertUnsupported(raw, "x.dng", "RAW");
    }

    [Theory]
    [InlineData(0xC3, -1, "lossless")]
    [InlineData(0xC9, -1, "arithmetic")]
    [InlineData(0xC1, 12, "12-bit")]
    public async Task Pipeline_UnsupportedJpegCoding(int sofMarker, int precision, string expected)
    {
        var input = TestImages.Write(_dir, "j.jpg", 16, 16, ColorSpace.sRGB, 8, 300);
        var bytes = await File.ReadAllBytesAsync(input);
        var sof = Enumerable.Range(0, bytes.Length - 1).First(i => bytes[i] == 0xFF && bytes[i + 1] == 0xC0);
        bytes[sof + 1] = (byte)sofMarker;
        if (precision > 0) bytes[sof + 4] = (byte)precision;
        await File.WriteAllBytesAsync(input, bytes);

        await AssertUnsupported(input, "j.jpg", expected);
    }

    [Fact]
    public async Task Pipeline_PreCancelled_ThrowsAndWritesNothing()
    {
        var input = TestImages.Write(_dir, "a.png", 20, 10, ColorSpace.sRGB, 16, 100);
        var output = OutPath("a.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ImagePipeline.RunAsync(input, output, ProcessingOptions.Default, cts.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Pipeline_OutputSameAsInput_Throws()
    {
        var input = TestImages.Write(_dir, "a.png", 20, 10, ColorSpace.sRGB, 16, 100);
        var before = await File.ReadAllBytesAsync(input);

        await Assert.ThrowsAsync<ArgumentException>(() => Run(input, input));
        Assert.Equal(before, await File.ReadAllBytesAsync(input));
    }

    private async Task AssertUnsupported(string input, string outputName, string expectedInMessage)
    {
        var output = OutPath(outputName);
        var ex = await Assert.ThrowsAsync<UnsupportedImageException>(() => Run(input, output));
        Assert.Contains(expectedInMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }
}
