using Illux.ImageTool.Options;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Settings;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Tests;

public sealed class ServiceControlTests
{
    [Theory]
    [InlineData("bob", @".\bob")]
    [InlineData(@"DOM\bob", @"DOM\bob")]
    [InlineData(@".\bob", @".\bob")]
    [InlineData("bob@example.com", "bob@example.com")]
    [InlineData("  bob  ", @".\bob")]
    [InlineData("", "LocalSystem")]
    [InlineData("   ", "LocalSystem")]
    [InlineData(null, "LocalSystem")]
    public void BuildAccountName(string? user, string expected) => Assert.Equal(expected, ServiceControl.BuildAccountName(user));

    [Theory]
    [InlineData(ServiceAction.Start, "--service start")]
    [InlineData(ServiceAction.Stop, "--service stop")]
    [InlineData(ServiceAction.Account, "--service account")]
    public void ElevatedArguments(ServiceAction action, string expected)
    {
        Assert.Equal(expected, ServiceControl.BuildElevatedArguments(action));

        var info = ServiceControl.BuildElevatedStartInfo(@"C:\x\IlluxImageToolSettings.exe", action);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(expected, info.Arguments);
        Assert.Equal(@"C:\x\IlluxImageToolSettings.exe", info.FileName);
    }

    [Theory]
    [InlineData("--service", "start", true, ServiceAction.Start)]
    [InlineData("--SERVICE", "Account", true, ServiceAction.Account)]
    [InlineData("--service", "restart", false, ServiceAction.Start)]
    [InlineData("--service", "5", false, ServiceAction.Start)]
    [InlineData("--tray", "start", false, ServiceAction.Start)]
    public void ParseArguments(string first, string second, bool ok, ServiceAction expected)
    {
        Assert.Equal(ok, ServiceControl.TryParseArguments([first, second], out var action));
        if (ok) Assert.Equal(expected, action);
    }

    [Fact]
    public void ParseArguments_WrongCount()
    {
        Assert.False(ServiceControl.TryParseArguments([], out _));
        Assert.False(ServiceControl.TryParseArguments(["--service"], out _));
        Assert.False(ServiceControl.TryParseArguments(["--service", "start", "extra"], out _));
    }

    [Theory]
    [InlineData(0, "Not installed")]
    [InlineData(1, "Stopped")]
    [InlineData(4, "Running")]
    [InlineData(-1, "Unknown")]
    public void DescribeState(int state, string expected) => Assert.Equal(expected, ServiceControl.DescribeState(state));
}

public sealed class FolderDialogTests
{
    [Fact]
    public void Defaults_GiveExplicitOperations()
    {
        Assert.Equal(["8bit", "upsample=200", "baseline"], FolderDialog.ToParameters(new FolderOptions()));
    }

    [Fact]
    public void ToParameters_WritesEveryNonDefaultChoice()
    {
        var parameters = FolderDialog.ToParameters(new FolderOptions(
            EightBit: false, Upsample: true, Ppi: 300, Baseline: true,
            Output: OutputMode.Pdf, MissingPpi: MissingPpiPolicy.Skip, Quality: 80));

        Assert.Equal(["upsample=300", "baseline", "output=pdf", "missingppi=skip", "quality=80"], parameters);
        Assert.Equal(["8bit", "output=both", "missingppi=error"], FolderDialog.ToParameters(new FolderOptions(
            Upsample: false, Baseline: false, Output: OutputMode.Both, MissingPpi: MissingPpiPolicy.Error)));
    }

    [Fact]
    public void ToParameters_NoOperationIsRejected()
    {
        // No operation parameter at all would mean "all three" to the parser, so the dialog must never save that.
        Assert.Throws<InvalidOperationException>(() =>
            FolderDialog.ToParameters(new FolderOptions(EightBit: false, Upsample: false, Baseline: false)));
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
            var options = new FolderOptions(eight, up, 300, baseline, output, missing, 70);

            var parameters = FolderDialog.ToParameters(options);
            var back = FolderDialog.FromParameters(parameters);

            // An unchecked "upsample" has no ppi to remember; the dialog shows the default again.
            Assert.Equal(up ? options : options with { Ppi = 200 }, back);
            // What the dialog writes is what the watcher will understand, with the same meaning.
            var parsed = ParameterParser.Parse(parameters);
            Assert.Equal(eight, parsed.Operations.HasFlag(Operations.EightBit));
            Assert.Equal(up, parsed.Operations.HasFlag(Operations.Upsample));
            Assert.Equal(baseline, parsed.Operations.HasFlag(Operations.Baseline));
            Assert.Equal(output, parsed.Output);
            Assert.Equal(missing, parsed.MissingPpi);
            Assert.Equal(70, parsed.JpegQuality);
        }
    }

    [Fact]
    public void FromParameters_EmptyListMeansAllOperationsOnWithDefaults()
    {
        Assert.Equal(new FolderOptions(), FolderDialog.FromParameters([]));
        Assert.Equal(new FolderOptions(), FolderDialog.FromParameters(["output=same"]));
    }

    [Fact]
    public void FromParameters_OnlyListedOperationsAreOn()
    {
        var options = FolderDialog.FromParameters(["8bit", "UPSAMPLE=300", "missingppi=72"]);

        Assert.True(options.EightBit);
        Assert.True(options.Upsample);
        Assert.Equal(300, options.Ppi);
        Assert.False(options.Baseline);
        Assert.Equal(MissingPpiPolicy.Assume72, options.MissingPpi);
    }

    [Fact]
    public void FilterParameterSurvivesAnEdit()
    {
        var options = FolderDialog.FromParameters(["8bit", "filter=mitchell"]);
        Assert.Equal(["filter=mitchell"], options.Extra);

        Assert.Equal(["8bit", "filter=mitchell"], FolderDialog.ToParameters(options with { Upsample = false, Baseline = false }));
    }

    [Fact]
    public void FromParameters_UnknownParameterThrows()
    {
        Assert.Throws<ParameterException>(() => FolderDialog.FromParameters(["sharpen"]));
    }

    private static WatchedFolder Valid(Action<WatchedFolder>? change = null)
    {
        var folder = new WatchedFolder { Name = "A", In = @"C:\A\In", Out = @"C:\A\Out", Error = @"C:\A\Error" };
        change?.Invoke(folder);
        return folder;
    }

    [Fact]
    public void Validate_AcceptsAGoodFolder() => Assert.Null(FolderDialog.Validate(Valid()));

    [Fact]
    public void Validate_RequiresNameAndFolders()
    {
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.Name = " ")));
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.In = "")));
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.Out = "")));
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.Error = "")));
    }

    [Fact]
    public void Validate_FoldersMustDiffer()
    {
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.Out = @"c:\a\in\")));
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.Error = @"C:\A\Out")));
        Assert.NotNull(FolderDialog.Validate(Valid(f => f.Originals = @"C:\A\In")));
        Assert.Null(FolderDialog.Validate(Valid(f => f.Originals = @"C:\A\Originals")));
    }
}

[Collection("AppRoot")]
public sealed class RequestFlagsTests : AppRootTestBase
{
    [Fact]
    public void CheckNow_CreatesTheRequestsFolderAndTheFlag()
    {
        Assert.False(Directory.Exists(AppPaths.RequestsDir));

        RequestFlags.RequestCheckNow();

        Assert.True(File.Exists(AppPaths.CheckNowFlag));
        Assert.StartsWith(AppRoot, AppPaths.CheckNowFlag);
        Assert.False(File.Exists(AppPaths.InstallNowFlag));
        Assert.False(File.Exists(Path.Combine(AppRoot, "check-now.flag"))); // not in Root (R29)
    }

    [Fact]
    public void InstallNow_WritesOnlyTheInstallFlag_AndCanBeRepeated()
    {
        RequestFlags.RequestInstallNow();
        RequestFlags.RequestInstallNow();

        Assert.True(File.Exists(AppPaths.InstallNowFlag));
        Assert.False(File.Exists(AppPaths.CheckNowFlag));
    }
}

public sealed class UpdateViewTests
{
    private static readonly Version Installed = new(1, 0, 0);

    [Fact]
    public void NothingAvailable_NoBanner()
    {
        var view = UpdateView.From(new UpdateState(), Installed);

        Assert.False(view.ShowBanner);
        Assert.False(view.CanUpdate);
        Assert.Equal("", view.PhaseText);
    }

    [Fact]
    public void Available_ShowsBannerWithEnabledButton()
    {
        var view = UpdateView.From(new UpdateState { AvailableVersion = "1.2.0" }, Installed);

        Assert.True(view.ShowBanner);
        Assert.Equal("Version 1.2.0 is ready", view.BannerText);
        Assert.True(view.CanUpdate);
        Assert.False(view.Busy);
        Assert.Equal("1.2.0", view.VersionText);
    }

    [Theory]
    [InlineData(UpdatePhase.Downloading, "Downloading the update...")]
    [InlineData(UpdatePhase.WaitingForJobs, "Waiting for running jobs to finish...")]
    [InlineData(UpdatePhase.Installing, "Installing...")]
    public void Busy_ShowsPhaseAndDisablesTheButton(string phase, string text)
    {
        var view = UpdateView.From(new UpdateState { AvailableVersion = "1.2.0", Phase = phase }, Installed);

        Assert.True(view.ShowBanner);
        Assert.False(view.CanUpdate);
        Assert.True(view.Busy);
        Assert.Equal(text, view.PhaseText);
    }

    [Fact]
    public void Failed_ShowsTheErrorAndAllowsTryingAgain()
    {
        var view = UpdateView.From(new UpdateState { AvailableVersion = "1.2.0", Phase = UpdatePhase.Failed, Error = "Checksum mismatch" }, Installed);

        Assert.True(view.CanUpdate);
        Assert.False(view.Busy);
        Assert.Equal("The update failed: Checksum mismatch", view.PhaseText);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    [InlineData("garbage")]
    [InlineData("")]
    public void NotNewerOrUnreadable_NoBanner(string available)
    {
        Assert.False(UpdateView.From(new UpdateState { AvailableVersion = available }, Installed).ShowBanner);
    }
}

public sealed class TrayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-tray-" + Guid.NewGuid().ToString("N"));
    private static readonly Version Installed = new(1, 0, 0);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private TrayMemory Memory => new(Path.Combine(_dir, "sub", "tray.json"));

    [Fact]
    public void Later_IsRememberedPerVersion_AcrossInstances()
    {
        Assert.False(Memory.IsLater("1.2.0"));

        Memory.MarkLater("1.2.0");

        Assert.True(Memory.IsLater("1.2.0"));
        Assert.False(Memory.IsLater("1.3.0")); // a newer version is announced again
        Memory.MarkLater("1.3.0");
        Assert.True(Memory.IsLater("1.2.0"));
        Assert.True(Memory.IsLater("1.3.0"));
    }

    [Fact]
    public void CorruptMemoryFile_IsTreatedAsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "tray.json"), "{ not json");

        Assert.False(Memory.IsLater("1.2.0"));
        Memory.MarkLater("1.2.0");
        Assert.True(Memory.IsLater("1.2.0"));
    }

    [Fact]
    public void DefaultPath_IsUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Illux Image Tool", "tray.json"), TrayMemory.DefaultPath);
    }

    [Fact]
    public void Announce_NewVersion()
    {
        var state = new UpdateState { AvailableVersion = "1.2.0" };

        Assert.Equal("1.2.0", TrayPolicy.VersionToAnnounce(state, Installed, Memory, null));
    }

    [Fact]
    public void Announce_NotTwiceInOneSession_NotAfterLater_NotWhenNotNewer()
    {
        var state = new UpdateState { AvailableVersion = "1.2.0" };

        Assert.Null(TrayPolicy.VersionToAnnounce(state, Installed, Memory, "1.2.0"));

        Memory.MarkLater("1.2.0");
        Assert.Null(TrayPolicy.VersionToAnnounce(state, Installed, Memory, null));
        Assert.Equal("1.3.0", TrayPolicy.VersionToAnnounce(new UpdateState { AvailableVersion = "1.3.0" }, Installed, Memory, "1.2.0"));

        Assert.Null(TrayPolicy.VersionToAnnounce(new UpdateState(), Installed, Memory, null));
        Assert.Null(TrayPolicy.VersionToAnnounce(new UpdateState { AvailableVersion = "1.0.0" }, Installed, Memory, null));
    }
}

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.2.3+abcdef", 1, 2, 3)]
    [InlineData("1.2.3-beta.1+abc", 1, 2, 3)]
    [InlineData("0.0.0-dev", 0, 0, 0)]
    [InlineData("junk", 0, 0, 0)]
    [InlineData(null, 0, 0, 0)]
    public void Parse(string? text, int major, int minor, int build) => Assert.Equal(new Version(major, minor, build), AppVersion.Parse(text));
}
