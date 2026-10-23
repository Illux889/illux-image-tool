using ImageMagick;
using Illux.ImageTool.Jpeg;

namespace Illux.ImageTool.Tests;

public sealed class JpegTranTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-jpegtran-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void AssertPixelsIdentical(string a, string b)
    {
        using var ia = new MagickImage(a);
        using var ib = new MagickImage(b);
        Assert.Equal(ia.ColorSpace, ib.ColorSpace);
        Assert.Equal(0.0, ia.Compare(ib, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task ToBaseline_ProgressiveBecomesBaseline_PixelsIdentical()
    {
        var input = TestImages.Write(_dir, "p.jpg", 96, 64, ColorSpace.sRGB, 8, 300, progressive: true);
        var output = Path.Combine(_dir, "out.jpg");
        Assert.Equal(JpegCoding.Progressive, JpegInspector.Inspect(input).Coding);

        await JpegTran.ToBaselineAsync(input, output);

        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        AssertPixelsIdentical(input, output);
    }

    [Fact]
    public async Task ToBaseline_KeepsIccAndExif()
    {
        var input = TestImages.Write(_dir, "p.jpg", 64, 48, ColorSpace.sRGB, 8, 300, progressive: true, icc: TestImages.SrgbIcc);
        using (var img = new MagickImage(input))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Artist, "Illux test");
            img.SetProfile(exif);
            img.Settings.Interlace = Interlace.Jpeg;
            img.Write(input);
        }
        var output = Path.Combine(_dir, "out.jpg");

        await JpegTran.ToBaselineAsync(input, output);

        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        using var result = new MagickImage(output);
        Assert.Equal(TestImages.SrgbIcc, result.GetProfile("icc")!.ToByteArray());
        var outExif = result.GetExifProfile();
        Assert.NotNull(outExif);
        Assert.Equal("Illux test", outExif!.GetValue(ExifTag.Artist)!.Value);
    }

    [Fact]
    public async Task ToBaseline_CmykProgressive()
    {
        var input = TestImages.Write(_dir, "c.jpg", 64, 48, ColorSpace.CMYK, 8, 300, progressive: true, icc: TestImages.CmykIcc);
        var output = Path.Combine(_dir, "out.jpg");
        Assert.Equal(JpegCoding.Progressive, JpegInspector.Inspect(input).Coding);

        await JpegTran.ToBaselineAsync(input, output);

        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        AssertPixelsIdentical(input, output);
    }

    [Fact]
    public async Task ToBaseline_PathWithSpacesAndDanishLetters()
    {
        var sub = Path.Combine(_dir, "mappe æøå");
        var input = TestImages.Write(sub, "fil 1.jpg", 48, 48, ColorSpace.sRGB, 8, 300, progressive: true);
        var output = Path.Combine(sub, "ud æøå 2.jpg");

        await JpegTran.ToBaselineAsync(input, output);

        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        AssertPixelsIdentical(input, output);
    }

    [Fact]
    public async Task ToBaseline_CorruptInput_Throws()
    {
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "corrupt.jpg");
        await File.WriteAllBytesAsync(input, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var output = Path.Combine(_dir, "out.jpg");

        var ex = await Assert.ThrowsAsync<JpegTranException>(() => JpegTran.ToBaselineAsync(input, output));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }
}
