using Illux.ImageTool.Options;
using Illux.ImageTool.Settings;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Tests;

/// <summary>The pure folder rules behind the folder window; on every OS, with that OS's kind of full path.</summary>
public sealed class FolderSettingsTests
{
    /// <summary>A full path on this OS: below C:\A on Windows, below /Users/Shared/A on macOS.</summary>
    internal static string FullPath(string name) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\A" : "/Users/Shared/A", name);

    [Fact]
    public void Defaults_GiveExplicitOperations()
    {
        Assert.Equal(["8bit", "resample", "baseline"], FolderSettings.ToParameters(new FolderOptions()));
    }

    [Fact]
    public void ToParameters_WritesEveryNonDefaultChoice()
    {
        var parameters = FolderSettings.ToParameters(new FolderOptions(
            EightBit: false, Resample: true, Baseline: true,
            Output: OutputMode.Pdf, MissingPpi: MissingPpiPolicy.Skip, Quality: 80));

        Assert.Equal(["resample", "baseline", "output=pdf", "missingppi=skip", "quality=80"], parameters);
        Assert.Equal(["8bit", "output=both", "missingppi=error"], FolderSettings.ToParameters(new FolderOptions(
            Resample: false, Baseline: false, Output: OutputMode.Both, MissingPpi: MissingPpiPolicy.Error)));
    }

    [Fact]
    public void ToParameters_NoOperationIsRejected()
    {
        // No operation parameter at all would mean "all three" to the parser, so the dialog must never save that.
        Assert.Throws<InvalidOperationException>(() =>
            FolderSettings.ToParameters(new FolderOptions(EightBit: false, Resample: false, Baseline: false)));
    }

    [Fact]
    public void RoundTrip_EveryCombination()
    {
        foreach (var eight in new[] { true, false })
        foreach (var up in new[] { true, false })
        foreach (var baseline in new[] { true, false })
        foreach (var output in Enum.GetValues<OutputMode>())
        foreach (var missing in Enum.GetValues<MissingPpiPolicy>())
        {
            if (!eight && !up && !baseline) continue;
            var options = new FolderOptions(eight, up, baseline, output, missing, 70);

            var parameters = FolderSettings.ToParameters(options);
            var back = FolderSettings.FromParameters(parameters);

            Assert.Equal(options, back);
            // What the dialog writes is what the watcher will understand, with the same meaning.
            var parsed = ParameterParser.Parse(parameters);
            Assert.Equal(eight, parsed.Operations.HasFlag(Operations.EightBit));
            Assert.Equal(up, parsed.Operations.HasFlag(Operations.Resample));
            Assert.Equal(baseline, parsed.Operations.HasFlag(Operations.Baseline));
            Assert.Equal(output, parsed.Output);
            Assert.Equal(missing, parsed.MissingPpi);
            Assert.Equal(70, parsed.JpegQuality);
        }
    }

    [Fact]
    public void FromParameters_EmptyListMeansAllOperationsOnWithDefaults()
    {
        Assert.Equal(new FolderOptions(), FolderSettings.FromParameters([]));
        Assert.Equal(new FolderOptions(), FolderSettings.FromParameters(["output=same"]));
    }

    [Fact]
    public void FromParameters_OnlyListedOperationsAreOn()
    {
        var options = FolderSettings.FromParameters(["8bit", "UPSAMPLE", "missingppi=72"]);

        Assert.True(options.EightBit);
        Assert.True(options.Resample);
        Assert.False(options.Baseline);
        Assert.Equal(MissingPpiPolicy.Assume72, options.MissingPpi);
    }

    [Fact]
    public void FilterParameterSurvivesAnEdit()
    {
        var options = FolderSettings.FromParameters(["8bit", "filter=mitchell"]);
        Assert.Equal(["filter=mitchell"], options.Extra);

        Assert.Equal(["8bit", "filter=mitchell"], FolderSettings.ToParameters(options with { Resample = false, Baseline = false }));
    }

    [Fact]
    public void FromParameters_UnknownParameterThrows()
    {
        Assert.Throws<ParameterException>(() => FolderSettings.FromParameters(["sharpen"]));
    }

    private static WatchedFolder Valid(Action<WatchedFolder>? change = null)
    {
        var folder = new WatchedFolder { Name = "A", In = FullPath("In"), Out = FullPath("Out"), Error = FullPath("Error") };
        change?.Invoke(folder);
        return folder;
    }

    [Fact]
    public void Validate_AcceptsAGoodFolder() => Assert.Null(FolderSettings.Validate(Valid()));

    [Fact]
    public void Validate_RequiresNameAndFolders()
    {
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.Name = " ")));
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.In = "")));
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.Out = "")));
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.Error = "")));
    }

    [Fact]
    public void Validate_FoldersMustDiffer()
    {
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.Out = FullPath("In").ToLowerInvariant() + Path.DirectorySeparatorChar)));
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.Error = FullPath("Out"))));
        Assert.NotNull(FolderSettings.Validate(Valid(f => f.Originals = FullPath("In"))));
        Assert.Null(FolderSettings.Validate(Valid(f => f.Originals = FullPath("Originals"))));
    }
}

public sealed class FolderPathValidationTests
{
    private static WatchedFolder Folder(string? inFolder = null, string? outFolder = null, string? error = null, string? originals = null) =>
        new()
        {
            Name = "A",
            In = inFolder ?? FolderSettingsTests.FullPath("In"),
            Out = outFolder ?? FolderSettingsTests.FullPath("Out"),
            Error = error ?? FolderSettingsTests.FullPath("Error"),
            Originals = originals,
        };

    [Theory]
    [InlineData(@"In")]
    [InlineData(@"..\In")]
    [InlineData(@".\In")]
    [InlineData(@"C:In")]
    [InlineData(@"\In")]
    public void RelativeOrDriveRelativePath_IsRejected(string path)
    {
        var message = FolderSettings.Validate(Folder(inFolder: path));
        Assert.NotNull(message);
        Assert.Contains("In folder is not a full path", message);

        Assert.Contains("Out folder is not a full path", FolderSettings.Validate(Folder(outFolder: path)));
        Assert.Contains("Error folder is not a full path", FolderSettings.Validate(Folder(error: path)));
        Assert.Contains("Originals folder is not a full path", FolderSettings.Validate(Folder(originals: path)));
    }

    [Fact]
    public void EmptyPath_IsStillAskedFor()
    {
        Assert.Equal("Choose the In folder.", FolderSettings.Validate(Folder(inFolder: "")));
        Assert.Equal("Choose the Error folder.", FolderSettings.Validate(Folder(error: "  ")));
    }

    [Fact]
    public void PathWithNul_IsRejected_NotThrown()
    {
        var message = FolderSettings.Validate(Folder(inFolder: "C:\\A\0\\In"));

        Assert.NotNull(message);
        Assert.Contains("not a full path", message);
    }

    [WindowsFact]
    public void FullAndUncPaths_AreAccepted()
    {
        Assert.Null(FolderSettings.Validate(Folder(@"\\srv\share\In", @"\\srv\share\Out", @"\\srv\share\Error", @"\\srv\share\Originals")));
        Assert.Null(FolderSettings.Validate(Folder(@"D:\x\In\", @"D:\x\Out\", @"D:\x\Error\")));
    }

    [WindowsFact]
    public void Windows_NotAFullPath_GivesAWindowsExample() =>
        Assert.Contains(@"for example C:\Folder or \\server\share\Folder", FolderSettings.Validate(Folder(inFolder: "x")));

    [MacFact]
    public void Mac_FullPaths_AreAccepted()
    {
        Assert.Null(FolderSettings.Validate(Folder("/Users/Shared/hot/In", "/Users/Shared/hot/Out", "/Users/Shared/hot/Error", "/Users/Shared/hot/Originals")));
        Assert.Null(FolderSettings.Validate(Folder("/Volumes/Share/In/", "/Volumes/Share/Out/", "/Volumes/Share/Error/")));
    }

    [MacFact]
    public void Mac_NotAFullPath_GivesAMacExample()
    {
        var message = FolderSettings.Validate(Folder(inFolder: "Shared/In"));

        Assert.Equal("The In folder is not a full path (for example /Users/Shared/Folder).", message);
        Assert.NotNull(FolderSettings.Validate(Folder(inFolder: @"C:\A\In"))); // a Windows path is not a full path on macOS
    }

    [MacFact]
    public void Mac_FoldersMustDiffer_IgnoringCaseAndTrailingSlash() =>
        Assert.Equal("The In and Out folders must be different.",
            FolderSettings.Validate(Folder("/Users/Shared/hot/In", "/users/shared/HOT/in/", "/Users/Shared/hot/Error")));

    [Fact]
    public void RelativeIsReportedBeforeDuplicates()
    {
        Assert.Contains("not a full path", FolderSettings.Validate(Folder(inFolder: "x", outFolder: "x")));
    }
}
