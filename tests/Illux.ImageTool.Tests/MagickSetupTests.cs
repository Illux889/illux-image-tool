using ImageMagick;
using Illux.ImageTool.Imaging;

namespace Illux.ImageTool.Tests;

public sealed class MagickSetupTests
{
    [Fact]
    public void Configure_IsIdempotent_SetsMemoryLimitAndTempDirectory()
    {
        MagickSetup.Configure();
        MagickSetup.Configure();

        var expected = (ulong)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2);
        Assert.Equal(expected, ResourceLimits.Memory);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "IlluxImageTool"), MagickSetup.TempDirectory);
        Assert.True(Directory.Exists(MagickSetup.TempDirectory));
    }
}
