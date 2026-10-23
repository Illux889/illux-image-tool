using ImageMagick;
using Illux.ImageTool.Imaging;

namespace Illux.ImageTool.Tests;

public sealed class ResolutionReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-resolution-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static Resolution? Read(string path)
    {
        using var image = new MagickImage(path);
        return ResolutionReader.Read(image);
    }

    private static void AssertResolution(double x, double y, Resolution? actual, double tolerance = 0.01)
    {
        Assert.NotNull(actual);
        Assert.Equal(x, actual!.X, tolerance);
        Assert.Equal(y, actual.Y, tolerance);
    }

    [Fact]
    public void Read_JpegDensity150()
    {
        var path = TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, 150);
        AssertResolution(150, 150, Read(path));
    }

    [Fact]
    public void Read_PngPhys()
    {
        var path = TestImages.Write(_dir, "a.png", 32, 32, ColorSpace.sRGB, 8, 300);
        AssertResolution(300, 300, Read(path), 0.1); // pHYs stores whole pixels per metre
    }

    [Fact]
    public void Read_TiffDpcm()
    {
        var path = TestImages.Write(_dir, "a.tif", 32, 32, ColorSpace.sRGB, 8, null);
        using (var image = new MagickImage(path))
        {
            image.Density = new Density(59.055, 59.055, DensityUnit.PixelsPerCentimeter);
            image.Write(path);
        }
        AssertResolution(150, 150, Read(path), 0.1);
    }

    [Fact]
    public void Read_NoDensity_IsNull()
    {
        Assert.Null(Read(TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, null))); // JFIF units 0, 1:1
        Assert.Null(Read(TestImages.Write(_dir, "a.png", 32, 32, ColorSpace.sRGB, 8, null))); // no pHYs
    }

    [Fact]
    public void Read_NonSquareDensity()
    {
        var path = TestImages.Write(_dir, "a.tif", 32, 32, ColorSpace.sRGB, 8, null);
        using (var image = new MagickImage(path))
        {
            image.Density = new Density(150, 300, DensityUnit.PixelsPerInch);
            image.Write(path);
        }
        AssertResolution(150, 300, Read(path));
    }

    [Fact]
    public void Read_ExifWinsOverJfif()
    {
        var path = TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, 300);
        JpegSegments.Insert(path, JpegSegments.Exif(100));
        AssertResolution(100, 100, Read(path));
    }

    [Fact]
    public void Read_ExifInCentimetres_ConvertedToPpi()
    {
        var path = TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, null);
        JpegSegments.Insert(path, JpegSegments.Exif(59.055, unit: 3));
        AssertResolution(150, 150, Read(path), 0.1);
    }

    [Fact]
    public void Read_PhotoshopResolutionWinsOverExifAndJfif()
    {
        var path = TestImages.Write(_dir, "a.jpg", 32, 32, ColorSpace.sRGB, 8, 300);
        JpegSegments.Insert(path, JpegSegments.Exif(100), JpegSegments.PhotoshopResolution(150));
        AssertResolution(150, 150, Read(path));
    }

    [Fact]
    public void From8Bim_SkipsOtherResourcesAndOddNames()
    {
        // A 0x0404 resource with a 3-character name (Pascal string 4 bytes, already even) and odd-sized data (padded).
        byte[] other = [.. "8BIM"u8.ToArray(), 0x04, 0x04, 3, (byte)'a', (byte)'b', (byte)'c', 0, 0, 0, 3, 1, 2, 3, 0];
        var data = other.Concat(JpegSegments.ResolutionInfo(240)).ToArray();
        AssertResolution(240, 240, ResolutionReader.From8Bim(data));
    }

    [Fact]
    public void From8Bim_TruncatedOrMissing_IsNull()
    {
        var block = JpegSegments.ResolutionInfo(240);
        Assert.Null(ResolutionReader.From8Bim(block[..20]));
        Assert.Null(ResolutionReader.From8Bim(null));
        Assert.Null(ResolutionReader.From8Bim([]));
    }
}
