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
        Assert.Equal(JpegCoding.Progressive, JpegInspector.Inspect(input).Coding);

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

    [Fact]
    public async Task ToBaseline_NonAnsiFileNames()
    {
        // Characters outside cp1252 (ł, ź, ğ): jpegtran's own file API would mangle these to best-fit names.
        var input = TestImages.Write(_dir, "załódź ğ.jpg", 48, 48, ColorSpace.sRGB, 8, 300, progressive: true);
        var output = Path.Combine(_dir, "wyjście ğł.jpg");

        await JpegTran.ToBaselineAsync(input, output);

        Assert.True(File.Exists(output));
        Assert.Equal(new[] { Path.GetFileName(output) }, Directory.GetFiles(_dir, "wyj*").Select(Path.GetFileName));
        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(output).Coding);
        AssertPixelsIdentical(input, output);
    }

    [Fact]
    public async Task ToBaseline_TruncatedInput_ThrowsAndLeavesNoOutput()
    {
        var full = TestImages.Write(_dir, "full.jpg", 128, 96, ColorSpace.sRGB, 8, 300, progressive: true);
        var bytes = await File.ReadAllBytesAsync(full);
        var truncated = Path.Combine(_dir, "truncated.jpg");
        await File.WriteAllBytesAsync(truncated, bytes.AsSpan(0, bytes.Length * 6 / 10).ToArray());
        var output = Path.Combine(_dir, "out.jpg");

        await Assert.ThrowsAsync<JpegTranException>(() => JpegTran.ToBaselineAsync(truncated, output));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ToBaseline_CorruptInput_LeavesNoOutput()
    {
        Directory.CreateDirectory(_dir);
        var input = Path.Combine(_dir, "corrupt.jpg");
        await File.WriteAllBytesAsync(input, new byte[] { 1, 2, 3, 4 });
        var output = Path.Combine(_dir, "out.jpg");

        await Assert.ThrowsAsync<JpegTranException>(() => JpegTran.ToBaselineAsync(input, output));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ToBaseline_PreCancelled_ThrowsAndLeavesNoOutput()
    {
        var input = TestImages.Write(_dir, "p.jpg", 64, 48, ColorSpace.sRGB, 8, 300, progressive: true);
        var output = Path.Combine(_dir, "out.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JpegTran.ToBaselineAsync(input, output, cts.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ToBaseline_CancelledWhileRunning_ThrowsAndLeavesNoOutput()
    {
        var input = TestImages.Write(_dir, "big.jpg", 3000, 2000, ColorSpace.sRGB, 8, 300, progressive: true);
        var output = Path.Combine(_dir, "out.jpg");
        using var cts = new CancellationTokenSource();

        var task = JpegTran.ToBaselineAsync(input, output, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ToBaseline_MissingExe_MessagePointsToFetchScript()
    {
        var input = TestImages.Write(_dir, "p.jpg", 32, 32, ColorSpace.sRGB, 8, 300, progressive: true);
        var missing = Path.Combine(_dir, "nope", "jpegtran.exe");

        var ex = await Assert.ThrowsAsync<JpegTranException>(
            () => JpegTran.ToBaselineAsync(input, Path.Combine(_dir, "out.jpg"), missing, CancellationToken.None));

        Assert.Contains(@"build\fetch-jpegtran.ps1", ex.Message);
        Assert.Contains(missing, ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "out.jpg")));
    }
}
