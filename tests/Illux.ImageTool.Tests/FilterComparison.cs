using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Manual check of the resampling filters (spec 8.2). Remove the Skip locally, run it, and look at
/// <c>artifacts/filter-compare/</c> (git-ignored): each source upsampled 150→200 ppi with every filter, plus
/// 4× nearest-neighbour crops of a high-contrast area where ringing (halos next to edges) would show.
/// </summary>
/// <remarks>
/// Decision (2026-10-23, ImageMagick built-ins "logo:", "wizard:", "rose:" and the synthetic gradient, viewed as
/// 4× crops): at this 1.33× enlargement Lanczos and Bicubic (Catmull-Rom) look almost identical and crisp, Mitchell
/// is slightly softer, and none shows visible ringing along the black outlines. Lanczos stays the default.
/// Not yet compared with Photoshop's "Bicubic Smoother" output (no Photoshop on the build machine); redo this with
/// a real customer image processed in Photoshop when one is available.
/// </remarks>
public sealed class FilterComparison
{
    [Fact(Skip = "Manual visual check; writes artifacts/filter-compare/")]
    public async Task Upsample150To200_AllFilters()
    {
        var outDir = Path.Combine(RepoRoot(), "artifacts", "filter-compare");
        Directory.CreateDirectory(outDir);

        var sources = new Dictionary<string, string>
        {
            ["logo"] = WriteBuiltIn(outDir, "logo:", "logo-150ppi.png"),
            ["rose"] = WriteBuiltIn(outDir, "rose:", "rose-150ppi.png"),
            ["wizard"] = WriteBuiltIn(outDir, "wizard:", "wizard-150ppi.png"),
            ["gradient"] = TestImages.Write(outDir, "gradient-150ppi.png", 600, 400, ColorSpace.sRGB, 16, 150),
        };

        foreach (var (name, input) in sources)
        {
            foreach (var filter in Enum.GetValues<ResampleFilter>())
            {
                var output = Path.Combine(outDir, $"{name}-200ppi-{filter}.png");
                var options = ProcessingOptions.Default with { Filter = filter };
                var result = await ImagePipeline.RunAsync(input, output, options, CancellationToken.None);
                Assert.NotEqual(Status.Error, result.Status);

                using var image = new MagickImage(output);
                image.Crop(new MagickGeometry((int)image.Width / 3, (int)image.Height / 3, 120, 90));
                image.ResetPage();
                image.Resize(new MagickGeometry(480, 360) { IgnoreAspectRatio = true }, FilterType.Point);
                image.Write(Path.Combine(outDir, $"{name}-200ppi-{filter}-zoom.png"));
            }
        }
    }

    private static string WriteBuiltIn(string dir, string builtIn, string name)
    {
        using var image = new MagickImage(builtIn);
        image.Density = new Density(150, 150, DensityUnit.PixelsPerInch);
        var path = Path.Combine(dir, name);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IlluxImageTool.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("IlluxImageTool.sln not found above the test output directory.");
    }
}
