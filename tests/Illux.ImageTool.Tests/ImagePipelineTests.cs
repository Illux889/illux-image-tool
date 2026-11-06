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
    public async Task RunWithResolution_IgnoresStoredPpi()
    {
        // 1181 x 886 px at the drawn 150 ppi is 20 x 15 cm; the stored 72 ppi must play no part.
        var input = TestImages.Write(_dir, "d.png", 1181, 886, ColorSpace.sRGB, 8, 72);
        var output = OutPath("d.png");

        var result = await ImagePipeline.RunAsync(input, output, ProcessingOptions.Default, new Resolution(150, 150), CancellationToken.None);

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        Assert.Contains(result.Messages, m => m.StartsWith("150→300 ppi (rule \"Small\": 20.0 × 15.0 cm;", StringComparison.Ordinal));
        using var image = new MagickImage(output);
        Assert.Equal(2362u, image.Width);
        Assert.Equal(1772u, image.Height);
        Assert.Equal(300, PpiX(image), 0.01);
    }

    [Theory]
    [InlineData(MissingPpiPolicy.Assume72)]
    [InlineData(MissingPpiPolicy.Skip)]
    [InlineData(MissingPpiPolicy.Error)]
    public async Task RunWithResolution_NoStoredPpi_NoMissingPpiWarning(MissingPpiPolicy policy)
    {
        var input = TestImages.Write(_dir, "n.png", 1181, 886, ColorSpace.sRGB, 8, null);
        var output = OutPath("n.png");

        var result = await ImagePipeline.RunAsync(input, output, Options(missing: policy) with { Output = OutputMode.Pdf },
            new Resolution(150, 150), CancellationToken.None);

        Assert.Equal(Status.Ok, result.Status);
        Assert.DoesNotContain(result.Messages, m => m.Contains("no resolution stored", StringComparison.Ordinal));
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
    }

    [Fact]
    public async Task RunWithResolution_InsideRange_CopiesUnchanged()
    {
        var input = TestImages.Write(_dir, "c.png", 300, 200, ColorSpace.sRGB, 8, 72);
        var output = OutPath("c.png");

        var result = await ImagePipeline.RunAsync(input, output, ProcessingOptions.Default, new Resolution(300, 300), CancellationToken.None);

        Assert.Equal(Status.Ok, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(["ppi inside 250–350 (rule \"Small\") — not resampled", "no change needed"], result.Messages);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Pipeline_Tiff16Rgb150_To8Bit300()
    {
        var input = TestImages.Write(_dir, "a.tif", 300, 200, ColorSpace.sRGB, 16, 150, icc: TestImages.SrgbIcc);
        Rewrite(input, i => i.Settings.Compression = CompressionMethod.LZW);
        var output = OutPath("a.tif");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(output, result.OutputPath);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        Assert.Contains("16→8 bit", result.Messages);
        Assert.Contains("150→300 ppi (rule \"Small\": 5.1 × 3.4 cm; 300×200 → 600×400, bicubic)", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(MagickFormat.Tiff, image.Format);
        Assert.Equal(8u, image.Depth);
        Assert.Equal(600u, image.Width);
        Assert.Equal(400u, image.Height);
        Assert.Equal(300, PpiX(image), 0.01);
        Assert.Equal(300, PpiY(image), 0.01);
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
        Assert.Equal(["16→8 bit", "ppi inside 250–350 (rule \"Small\") — not resampled"], result.Messages);
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
        Assert.Equal(120u, image.Width);
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
        Assert.Equal(180u, b.Width);
        // Same colours after enlargement: compare against the input enlarged the same way (Bicubic = Catrom).
        a.Resize(new MagickGeometry(180, 120) { IgnoreAspectRatio = true }, FilterType.Catrom);
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
        Assert.Equal(["ppi inside 250–350 (rule \"Small\") — not resampled", "progressive→baseline (lossless)"], result.Messages);
        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        using var a = new MagickImage(input);
        using var b = new MagickImage(output);
        Assert.Equal(0.0, a.Compare(b, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Pipeline_ProgressiveJpeg100ppi_ReencodedBaseline300()
    {
        var input = TestImages.Write(_dir, "p.jpg", 90, 60, ColorSpace.sRGB, 8, 100, progressive: true);
        var output = OutPath("p.jpg");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains(result.Messages, m => m.Contains("re-encoded, q95"));
        Assert.Contains("100→300 ppi (rule \"Small\": 2.3 × 1.5 cm; 90×60 → 270×180, bicubic)", result.Messages);
        var info = JpegInspector.Inspect(output);
        Assert.Equal(JpegCoding.Baseline, info.Coding);
        Assert.Equal((300.0, 300.0, 1), info.JfifDensity);
        using var image = new MagickImage(output);
        Assert.Equal(270u, image.Width);
        Assert.Equal(180u, image.Height);
    }

    [Fact]
    public async Task Pipeline_ProgressiveJpeg_ResampleWithBaselineOff_StaysProgressive()
    {
        // R14: re-encoding keeps the input's coding when the Baseline operation is off.
        var input = TestImages.Write(_dir, "p.jpg", 90, 60, ColorSpace.sRGB, 8, 100, progressive: true);
        var output = OutPath("p.jpg");

        var result = await Run(input, output, Options(Operations.EightBit | Operations.Resample));

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains("re-encoded (q95)", result.Messages);
        Assert.DoesNotContain(result.Messages, m => m.Contains("baseline"));
        var info = JpegInspector.Inspect(output);
        Assert.Equal(JpegCoding.Progressive, info.Coding);
        Assert.Equal((300.0, 300.0, 1), info.JfifDensity);
        Assert.Equal(270, info.Width);
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
        Assert.Equal(["ppi inside 250–350 (rule \"Small\") — not resampled", "no change needed"], result.Messages);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task Pipeline_InsideRange_NotResampled()
    {
        var input = TestImages.Write(_dir, "a.tif", 60, 40, ColorSpace.sRGB, 16, 340);
        var output = OutPath("a.tif");

        var result = await Run(input, output);

        Assert.Equal(["16→8 bit", "ppi inside 250–350 (rule \"Small\") — not resampled"], result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(60u, image.Width);
        Assert.Equal(40u, image.Height);
        Assert.Equal(340, PpiX(image), 0.01);
    }

    [Fact]
    public async Task Pipeline_AboveRange_Downscaled_WithBicubic()
    {
        var input = TestImages.Write(_dir, "d.tif", 120, 60, ColorSpace.sRGB, 8, 600);
        var output = OutPath("d.tif");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal(["600→300 ppi (rule \"Small\": 0.5 × 0.3 cm; 120×60 → 60×30, bicubic)"], result.Messages);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(60u, image.Width);
        Assert.Equal(30u, image.Height);
        Assert.Equal(300, PpiX(image), 0.01);
        Assert.Equal(300, PpiY(image), 0.01);
    }

    [Fact]
    public async Task Pipeline_Png200ppi_NotResampledForPhysRounding()
    {
        // pHYs holds whole pixels per metre: 200 ppi is stored as 7874 px/m and reads back as 199.9996 ppi.
        var input = TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 8, 200);
        var output = OutPath("a.png");

        // A rule whose leave-alone range is exactly 200: 199.9996 must count as inside it.
        IReadOnlyList<ResolutionRule> exactly200 = [new("Only", SizeTest.All, null, null, null, null, 200, 200, 200)];
        var result = await Run(input, output, ProcessingOptions.Default with { Rules = exactly200 });

        Assert.False(result.Changed);
        Assert.Equal(["ppi inside 200–200 (rule \"Only\") — not resampled", "no change needed"], result.Messages);
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

        Assert.Equal(Status.Warning, result.Status); // something not asked for changed: say so (final review M2)
        Assert.Contains("Photoshop layers removed (flattened image kept)", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(8u, image.Depth);
        Assert.Null(image.GetProfile("tiff:37724"));
    }

    [Fact]
    public async Task Pipeline_OnlyBaselineOperation_DoesNotResample()
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
        Assert.Contains(result.Messages, m => m.StartsWith("72→300 ppi (rule \"Small\": 2.5 × ", StringComparison.Ordinal)
            && m.EndsWith(" cm; 72×36 → 300×150, bicubic)", StringComparison.Ordinal));
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(300u, image.Width);
        Assert.Equal(150u, image.Height);
        Assert.Equal(300, PpiX(image), 0.1);
    }

    [Fact]
    public async Task Pipeline_MissingPpi_Skip_Warning()
    {
        var input = TestImages.Write(_dir, "n.png", 72, 36, ColorSpace.sRGB, 8, null);
        var output = OutPath("n.png");

        var result = await Run(input, output, Options(missing: MissingPpiPolicy.Skip));

        Assert.Equal(Status.Warning, result.Status);
        Assert.Contains("no resolution stored — resampling skipped", result.Messages);
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
    public async Task Pipeline_MissingPpi_ResampleOff_NoWarning()
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

    [Theory]
    [InlineData(OutputMode.Pdf)]
    [InlineData(OutputMode.Both)]
    public async Task Pipeline_MissingPpi_ResampleOff_PdfOutput_WarnsAboutThePageSize(OutputMode output)
    {
        var input = TestImages.Write(_dir, "n.png", 40, 30, ColorSpace.sRGB, 16, null);

        var result = await Run(input, OutPath("n.png"),
            Options(Operations.EightBit | Operations.Baseline, MissingPpiPolicy.Error) with { Output = output });

        Assert.Equal(Status.Warning, result.Status);
        Assert.Contains("no resolution stored — PDF page size assumes 72 ppi", result.Messages);
    }

    [Fact]
    public async Task Pipeline_FolderAsInput_SaysSo()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "a folder.jpg")).FullName;

        var ex = await Assert.ThrowsAsync<UnsupportedImageException>(() => Run(folder, OutPath("x.jpg")));

        Assert.Equal("is a folder, not a file", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pipeline_WebP_ReencodeKeepsLosslessOrLossy(bool lossless)
    {
        var input = Path.Combine(_dir, "w.webp");
        using (var source = new MagickImage(TestImages.Write(_dir, "w.png", 60, 40, ColorSpace.sRGB, 8, null)))
        {
            source.Settings.SetDefine(MagickFormat.WebP, "lossless", lossless);
            source.Write(input, MagickFormat.WebP);
        }
        Assert.Equal(lossless, WebPChunk(input) == "VP8L");

        var output = OutPath("w.webp");
        var result = await Run(input, output); // no resolution: 72 ppi assumed, resampled to 300

        Assert.NotEqual(Status.Error, result.Status);
        Assert.Equal(lossless ? "VP8L" : "VP8 ", WebPChunk(output));
    }

    /// <summary>The image chunk of a simple or extended WebP: "VP8L" (lossless) or "VP8 " (lossy).</summary>
    private static string WebPChunk(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (var pos = 12; pos + 8 <= bytes.Length;)
        {
            var fourcc = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            if (fourcc is "VP8L" or "VP8 ") return fourcc;
            var size = BitConverter.ToInt32(bytes, pos + 4);
            pos += 8 + size + (size & 1);
        }
        return "";
    }

    [Fact]
    public async Task Pipeline_Factor5_Warning()
    {
        var input = TestImages.Write(_dir, "f.png", 20, 10, ColorSpace.sRGB, 8, 60);
        var output = OutPath("f.png");

        var result = await Run(input, output);

        Assert.Equal(Status.Warning, result.Status);
        Assert.Contains("upscale factor 5× is above 4×", result.Messages);
        using var image = new MagickImage(output);
        Assert.Equal(100u, image.Width);
        Assert.Equal(50u, image.Height);
    }

    [Fact]
    public async Task Pipeline_NonSquare_OnlyOutOfRangeAxisResampled()
    {
        // R13: an axis inside the rule's range keeps its ppi and pixel count.
        var input = TestImages.Write(_dir, "n.tif", 150, 300, ColorSpace.sRGB, 8, null);
        Rewrite(input, i => i.Density = new Density(150, 320, DensityUnit.PixelsPerInch));
        var output = OutPath("n.tif");

        var result = await Run(input, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains("150×320→300×320 ppi (rule \"Small\": 2.5 × 2.4 cm; 150×300 → 300×300, bicubic)", result.Messages);
        Assert.Equal(new Resolution(300, 320), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(300u, image.Width);
        Assert.Equal(300u, image.Height);
        Assert.Equal(300, PpiX(image), 0.01);
        Assert.Equal(320, PpiY(image), 0.01);
    }

    [Fact]
    public async Task Pipeline_NonSquare_BothAxesLow_EachRaisedToTarget()
    {
        var input = TestImages.Write(_dir, "n.tif", 100, 150, ColorSpace.sRGB, 8, null);
        Rewrite(input, i => i.Density = new Density(100, 150, DensityUnit.PixelsPerInch));
        var output = OutPath("n.tif");

        var result = await Run(input, output);

        Assert.Contains("100×150→300 ppi (rule \"Small\": 2.5 × 2.5 cm; 100×150 → 300×300, bicubic)", result.Messages);
        Assert.Equal(new Resolution(300, 300), result.FinalResolution);
        using var image = new MagickImage(output);
        Assert.Equal(300u, image.Width);
        Assert.Equal(300u, image.Height);
        Assert.Equal(300, PpiY(image), 0.01);
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
        Assert.Equal(360u, image.Width);
        Assert.Equal(240u, image.Height);
        Assert.Equal((ushort)6, image.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
    }

    [Fact]
    public async Task Pipeline_Resample_RewritesExifAndPhotoshopResolution()
    {
        var input = TestImages.Write(_dir, "r.jpg", 50, 40, ColorSpace.sRGB, 8, 100);
        JpegSegments.Insert(input, JpegSegments.Exif(100), JpegSegments.PhotoshopResolution(100));
        var output = OutPath("r.jpg");

        await Run(input, output);

        Assert.Equal((300.0, 300.0, 1), JpegInspector.Inspect(output).JfifDensity);
        using var image = new MagickImage(output);
        Assert.Equal(150u, image.Width);
        var exif = image.GetExifProfile()!;
        Assert.Equal(300, exif.GetValue(ExifTag.XResolution)!.Value.ToDouble(), 0.01);
        Assert.Equal(300, exif.GetValue(ExifTag.YResolution)!.Value.ToDouble(), 0.01);
        Assert.Equal((ushort)2, exif.GetValue(ExifTag.ResolutionUnit)!.Value);
        var photoshop = ResolutionReader.From8Bim(image.GetProfile("8bim")!.ToByteArray());
        Assert.Equal(new Resolution(300, 300), photoshop);
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
        Assert.Equal(120u, image.Width);
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

    // --- the resolution-rule decision (spec 5.4), checked on the plan so large print sizes need no real pixels ---

    private static uint Px(double cm, double ppi) => (uint)Math.Round(cm / 2.54 * ppi, MidpointRounding.AwayFromZero);

    private static SourceImage Source(uint width, uint height, Resolution? resolution) =>
        new(MagickFormat.Tiff, width, height, 8, resolution, null);

    private static SourceImage Source(double widthCm, double heightCm, double ppi) =>
        Source(Px(widthCm, ppi), Px(heightCm, ppi), new Resolution(ppi, ppi));

    private static void AssertPhysicalSizeKept(SourceImage source, ImagePipeline.PlanResult plan)
    {
        var size = plan.NewSize!.Value;
        Assert.Equal(source.Width / source.Resolution!.X * 2.54, size.Width / plan.FinalResolution.X * 2.54, 0.1);
        Assert.Equal(source.Height / source.Resolution!.Y * 2.54, size.Height / plan.FinalResolution.Y * 2.54, 0.1);
    }

    [Fact]
    public void Plan_Small_Upscale_To300()
    {
        var source = Source(20, 15, 150);

        var plan = ImagePipeline.Plan(source, ProcessingOptions.Default);

        Assert.Null(plan.Error);
        Assert.False(plan.Warning);
        Assert.Equal(new Resolution(300, 300), plan.FinalResolution);
        Assert.Equal((2362u, 1772u), plan.NewSize);
        AssertPhysicalSizeKept(source, plan);
        Assert.Equal(["150→300 ppi (rule \"Small\": 20.0 × 15.0 cm; 1181×886 → 2362×1772, bicubic)"], plan.Messages);
    }

    [Fact]
    public void Plan_Small_Inside_250to350_Untouched()
    {
        var plan = ImagePipeline.Plan(Source(20, 15, 280), ProcessingOptions.Default);

        Assert.Null(plan.NewSize);
        Assert.False(plan.Warning);
        Assert.Equal(new Resolution(280, 280), plan.FinalResolution);
        Assert.Equal(["ppi inside 250–350 (rule \"Small\") — not resampled"], plan.Messages);
    }

    [Fact]
    public void Plan_Large_Downscale_To200()
    {
        var source = Source(60, 40, 300);

        var plan = ImagePipeline.Plan(source, ProcessingOptions.Default);

        Assert.False(plan.Warning);
        Assert.Equal(new Resolution(200, 200), plan.FinalResolution);
        Assert.Equal((4725u, 3149u), plan.NewSize);
        AssertPhysicalSizeKept(source, plan);
        Assert.Equal(["300→200 ppi (rule \"Large\": 60.0 × 40.0 cm; 7087×4724 → 4725×3149, bicubic)"], plan.Messages);
    }

    [Fact]
    public void Plan_Large_Inside_150to250_Untouched()
    {
        var plan = ImagePipeline.Plan(Source(60, 40, 240), ProcessingOptions.Default);

        Assert.Null(plan.NewSize);
        Assert.Equal(new Resolution(240, 240), plan.FinalResolution);
        Assert.Equal(["ppi inside 150–250 (rule \"Large\") — not resampled"], plan.Messages);
    }

    [Fact]
    public void Plan_Large_Upscale_To200()
    {
        var source = Source(50, 40, 100);

        var plan = ImagePipeline.Plan(source, ProcessingOptions.Default);

        Assert.False(plan.Warning);
        Assert.Equal(new Resolution(200, 200), plan.FinalResolution);
        Assert.Equal((3938u, 3150u), plan.NewSize);
        AssertPhysicalSizeKept(source, plan);
        Assert.Equal(["100→200 ppi (rule \"Large\": 50.0 × 40.0 cm; 1969×1575 → 3938×3150, bicubic)"], plan.Messages);
    }

    [Fact]
    public void Plan_OnlyOutOfRangeAxisChanges()
    {
        // Small (25.4 × 23.8 cm): x 150 ppi is below 250 and goes to 300; y 320 ppi is inside 250–350 and stays.
        var small = Source(1500, 3000, new Resolution(150, 320));
        var up = ImagePipeline.Plan(small, ProcessingOptions.Default);

        Assert.Equal(new Resolution(300, 320), up.FinalResolution);
        Assert.Equal((3000u, 3000u), up.NewSize);
        AssertPhysicalSizeKept(small, up);
        Assert.Equal(["150×320→300×320 ppi (rule \"Small\": 25.4 × 23.8 cm; 1500×3000 → 3000×3000, bicubic)"], up.Messages);

        // Large (63.5 × 63.5 cm): x 400 ppi is above 250 and goes to 200; y 200 ppi is inside 150–250 and stays.
        var large = Source(10000, 5000, new Resolution(400, 200));
        var down = ImagePipeline.Plan(large, ProcessingOptions.Default);

        Assert.Equal(new Resolution(200, 200), down.FinalResolution);
        Assert.Equal((5000u, 5000u), down.NewSize);
        AssertPhysicalSizeKept(large, down);
        Assert.Equal(["400×200→200 ppi (rule \"Large\": 63.5 × 63.5 cm; 10000×5000 → 5000×5000, bicubic)"], down.Messages);
    }

    [Fact]
    public void Plan_NoPpi_UsesAssumed72_LargeRule()
    {
        // 1200 × 100 px at the assumed 72 ppi print 42.3 × 3.5 cm: too long for 40 × 30, so the Large rule applies.
        var plan = ImagePipeline.Plan(Source(1200, 100, null), ProcessingOptions.Default);

        Assert.True(plan.Warning);
        Assert.Equal(new Resolution(200, 200), plan.FinalResolution);
        Assert.Equal((3333u, 278u), plan.NewSize);
        Assert.Equal(
            ["no resolution stored — assumed 72 ppi", "72→200 ppi (rule \"Large\": 42.3 × 3.5 cm; 1200×100 → 3333×278, bicubic)"],
            plan.Messages);
    }

    [Fact]
    public void Plan_DownscaleUsesBicubicByDefault()
    {
        var plan = ImagePipeline.Plan(Source(60, 40, 300), ProcessingOptions.Default);
        Assert.Contains(plan.Messages, m => m.EndsWith(", bicubic)", StringComparison.Ordinal));

        var lanczos = ImagePipeline.Plan(Source(60, 40, 300), ProcessingOptions.Default with { Filter = ResampleFilter.Lanczos });
        Assert.Contains(lanczos.Messages, m => m.EndsWith(", lanczos)", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_FactorAbove4_Warning_Upscale_Only()
    {
        // 300 / 60 = 5× enlargement: Warning.
        var up = ImagePipeline.Plan(Source(10, 10, 60), ProcessingOptions.Default);
        Assert.True(up.Warning);
        Assert.Contains("upscale factor 5× is above 4×", up.Messages);

        // 2400 → 300 is an 8× reduction: no Warning.
        var down = ImagePipeline.Plan(Source(5, 5, 2400), ProcessingOptions.Default);
        Assert.False(down.Warning);
        Assert.Equal(new Resolution(300, 300), down.FinalResolution);
        Assert.DoesNotContain(down.Messages, m => m.Contains("factor"));
    }

    [Fact]
    public void Plan_ResampleOff_NoRuleMessages()
    {
        var plan = ImagePipeline.Plan(Source(60, 40, 300), Options(Operations.EightBit | Operations.Baseline));

        Assert.Null(plan.NewSize);
        Assert.Empty(plan.Messages);
        Assert.Equal(new Resolution(300, 300), plan.FinalResolution);
    }

    [Fact]
    public void Plan_UsesTheRulesInTheOptions()
    {
        IReadOnlyList<ResolutionRule> rules =
        [
            new("Tiny", SizeTest.LongestSide, null, null, 5, null, 600, 600, 600),
            new("Rest", SizeTest.All, null, null, null, null, 100, 100, 100),
        ];
        var options = ProcessingOptions.Default with { Rules = rules };

        Assert.Equal(new Resolution(600, 600), ImagePipeline.Plan(Source(4, 3, 300), options).FinalResolution);
        Assert.Equal(new Resolution(100, 100), ImagePipeline.Plan(Source(20, 15, 300), options).FinalResolution);
    }

    [Fact]
    public void Plan_EnlargementBeyond65000Pixels_Throws()
    {
        var ex = Assert.Throws<ImageTooLargeException>(() => ImagePipeline.Plan(Source(400, 400, new Resolution(1, 1)), ProcessingOptions.Default));
        Assert.Contains("80000×80000", ex.Message);
    }

    private async Task AssertUnsupported(string input, string outputName, string expectedInMessage)
    {
        var output = OutPath(outputName);
        var ex = await Assert.ThrowsAsync<UnsupportedImageException>(() => Run(input, output));
        Assert.Contains(expectedInMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }
}
