using ImageMagick;

namespace Illux.ImageTool.Tests;

public sealed class TestImagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-testimages-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Write_SixteenBitAlphaPng_KeepsDepthAlphaAndDensity()
    {
        var path = TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 16, 100, alpha: true);
        using var image = new MagickImage(path);
        Assert.Equal(16u, image.Depth);
        Assert.True(image.HasAlpha);
        Assert.Equal(40u, image.Width);
        Assert.Equal(30u, image.Height);
        Assert.Equal(100, image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 0.5); // PNG stores pixels per cm
    }

    [Fact]
    public void Write_GrayAlphaPng_KeepsAlpha()
    {
        var path = TestImages.Write(_dir, "ga.png", 20, 10, ColorSpace.Gray, 16, 300, alpha: true);
        using var image = new MagickImage(path);
        Assert.Equal(ColorSpace.Gray, image.ColorSpace);
        Assert.True(image.HasAlpha);
    }

    [Fact]
    public void Write_ContainsGradientAndNoise()
    {
        var path = TestImages.Write(_dir, "g.png", 64, 64, ColorSpace.sRGB, 8, 72);
        using var image = new MagickImage(path);
        using var pixels = image.GetPixels();
        var left = pixels.GetPixel(0, 32).ToColor()!;
        var right = pixels.GetPixel(63, 32).ToColor()!;
        Assert.True(right.R - left.R > 100 * 257); // red ramps left to right (16-bit quantum)
        Assert.NotEqual(pixels.GetPixel(10, 10).ToColor()!.B, pixels.GetPixel(11, 10).ToColor()!.B); // noise: neighbours differ
    }
}
