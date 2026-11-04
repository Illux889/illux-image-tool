using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Tests;

public sealed class MagickSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-magicktemp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Configure_IsIdempotent_SetsMemoryLimitAndTempDirectory()
    {
        MagickSetup.Configure();
        MagickSetup.Configure();

        var expected = (ulong)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2);
        Assert.Equal(expected, ResourceLimits.Memory);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "IlluxImageTool"), MagickSetup.TempDirectory);
        Assert.True(Directory.Exists(MagickSetup.TempDirectory));
        Assert.Null(MagickSetup.TempDirectoryProblem());
    }

    // ----- final review I2: ImageMagick's temp folder on macOS -----

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void TempDirectory_OnMac_Missing_IsCreatedOwnerOnly()
    {
        var temp = Path.Combine(_dir, "IlluxImageTool");
        Directory.CreateDirectory(_dir);

        Assert.Null(MagickSetup.CheckTempDirectory(temp));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(temp));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void TempDirectory_OnMac_SquattedAsAFile_IsRefused()
    {
        var temp = Path.Combine(_dir, "IlluxImageTool");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(temp, "planted");

        Assert.Contains("is not a folder", MagickSetup.CheckTempDirectory(temp));
        Assert.Equal("planted", File.ReadAllText(temp));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void TempDirectory_OnMac_LinkOrOpenFolder_IsRefused()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, elsewhere);
        var open = Path.Combine(_dir, "open");
        Directory.CreateDirectory(open);
        File.SetUnixFileMode(open, (UnixFileMode)0b111_111_111);

        Assert.Contains("is a link", MagickSetup.CheckTempDirectory(link));
        Assert.Contains("writable by group or others", MagickSetup.CheckTempDirectory(open));
    }

    [WindowsFact]
    public void TempDirectory_OnWindows_IsNotChecked()
    {
        var temp = Path.Combine(_dir, "IlluxImageTool");

        Assert.Null(MagickSetup.CheckTempDirectory(temp));
        Assert.True(Directory.Exists(temp));
    }
}

/// <summary>Changes <see cref="MagickSetup.TempCheck"/> for the whole process: runs alone (AppRoot collection).</summary>
[Collection("AppRoot")]
public sealed class UnsafeMagickTempTests : AppRootTestBase
{
    [Fact]
    public async Task UnsafeTempFolder_FileIsRefused_WithTheReason()
    {
        var input = TestImages.Write(Path.Combine(Dir, "in"), "a.jpg", 60, 40, ColorSpace.sRGB, 8, 300);
        var previous = MagickSetup.TempCheck;
        MagickSetup.TempCheck = () => "ImageMagick's temporary folder '/tmp/IlluxImageTool' is not a folder; files are not processed until it is fixed.";
        try
        {
            var result = await FileProcessor.ProcessAsync(
                input, Path.Combine(Dir, "out"), ProcessingOptions.Default, NamePolicy.Suffix, new HashSet<string>(), CancellationToken.None);

            Assert.Equal(Status.Error, result.Status);
            Assert.Contains("ImageMagick's temporary folder '/tmp/IlluxImageTool' is not a folder", Assert.Single(result.Messages));
            Assert.False(Directory.Exists(Path.Combine(Dir, "out")));
            Assert.True(File.Exists(input));
        }
        finally
        {
            MagickSetup.TempCheck = previous;
        }
    }
}
