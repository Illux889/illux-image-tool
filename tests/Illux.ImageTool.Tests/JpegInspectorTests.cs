using ImageMagick;
using Illux.ImageTool.Jpeg;

namespace Illux.ImageTool.Tests;

public sealed class JpegInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-jpeginspector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Inspect_Baseline_Rgb()
    {
        var path = TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 72);
        var info = JpegInspector.Inspect(path);
        Assert.Equal(JpegCoding.Baseline, info.Coding);
        Assert.Equal(8, info.Precision);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(3, info.Components);
    }

    [Fact]
    public void Inspect_Progressive()
    {
        var path = TestImages.Write(_dir, "p.jpg", 64, 48, ColorSpace.sRGB, 8, 72, progressive: true);
        Assert.Equal(JpegCoding.Progressive, JpegInspector.Inspect(path).Coding);
    }

    [Fact]
    public void Inspect_Gray_HasOneComponent()
    {
        var path = TestImages.Write(_dir, "g.jpg", 32, 32, ColorSpace.Gray, 8, 72);
        Assert.Equal(1, JpegInspector.Inspect(path).Components);
    }

    [Fact]
    public void Inspect_CmykFromMagick_HasAdobeMarker()
    {
        // Verified by dumping the bytes: ImageMagick writes an Adobe APP14 marker ("Adobe", version 100,
        // flags 0/0) for CMYK JPEGs, with transform byte 2 (YCCK), not 0 (plain CMYK).
        var path = TestImages.Write(_dir, "c.jpg", 64, 48, ColorSpace.CMYK, 8, 72);
        var info = JpegInspector.Inspect(path);
        Assert.Equal(4, info.Components);
        Assert.True(info.HasAdobeMarker);
        Assert.Equal(2, info.AdobeTransform);
    }

    [Fact]
    public void Inspect_Rgb_HasNoAdobeMarker()
    {
        var path = TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, 72);
        var info = JpegInspector.Inspect(path);
        Assert.False(info.HasAdobeMarker);
        Assert.Equal(-1, info.AdobeTransform);
    }

    [Fact]
    public void Inspect_Density()
    {
        var path = TestImages.Write(_dir, "d.jpg", 64, 48, ColorSpace.sRGB, 8, 150);
        var info = JpegInspector.Inspect(path);
        Assert.NotNull(info.JfifDensity);
        Assert.Equal(150, info.JfifDensity!.Value.X);
        Assert.Equal(150, info.JfifDensity.Value.Y);
        Assert.Equal(1, info.JfifDensity.Value.Units);
    }

    [Fact]
    public void Inspect_NoPpi_HasNoUsableDensity()
    {
        var path = TestImages.Write(_dir, "n.jpg", 64, 48, ColorSpace.sRGB, 8, null);
        // Verified by dumping the bytes: Density(0,0) makes ImageMagick write JFIF units 0 (aspect ratio only), 1:1.
        var density = JpegInspector.Inspect(path).JfifDensity;
        Assert.Equal((1d, 1d, 0), density);
    }

    [Fact]
    public void Inspect_IccRoundTrip()
    {
        var path = TestImages.Write(_dir, "i.jpg", 64, 48, ColorSpace.sRGB, 8, 72, icc: TestImages.SrgbIcc);
        Assert.Equal(TestImages.SrgbIcc, JpegInspector.Inspect(path).IccProfile);
    }

    [Fact]
    public void Inspect_LargeIccIsReassembledFromMultipleChunks()
    {
        // The CMYK profile is far larger than the 65519-byte limit of one APP2 chunk.
        Assert.True(TestImages.CmykIcc.Length > 65519);
        var path = TestImages.Write(_dir, "ci.jpg", 64, 48, ColorSpace.CMYK, 8, 72, icc: TestImages.CmykIcc);
        Assert.Equal(TestImages.CmykIcc, JpegInspector.Inspect(path).IccProfile);
    }

    [Fact]
    public void Inspect_NoIcc_IsNull()
    {
        var path = TestImages.Write(_dir, "x.jpg", 32, 32, ColorSpace.sRGB, 8, 72);
        Assert.Null(JpegInspector.Inspect(path).IccProfile);
    }

    [Fact]
    public void Inspect_NotJpeg_Throws()
    {
        var path = TestImages.Write(_dir, "a.png", 16, 16, ColorSpace.sRGB, 8, 72);
        Assert.Throws<InvalidDataException>(() => JpegInspector.Inspect(path));
    }
}
