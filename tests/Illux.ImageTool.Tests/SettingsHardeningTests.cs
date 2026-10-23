using Illux.ImageTool.Runtime;
using Illux.ImageTool.Settings;
using Illux.ImageTool.Update;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Tests;

public sealed class FolderPathValidationTests
{
    private static WatchedFolder Folder(string inFolder = @"C:\A\In", string outFolder = @"C:\A\Out", string error = @"C:\A\Error", string? originals = null) =>
        new() { Name = "A", In = inFolder, Out = outFolder, Error = error, Originals = originals };

    [Theory]
    [InlineData(@"In")]
    [InlineData(@"..\In")]
    [InlineData(@".\In")]
    [InlineData(@"C:In")]
    [InlineData(@"\In")]
    public void RelativeOrDriveRelativePath_IsRejected(string path)
    {
        var message = FolderDialog.Validate(Folder(inFolder: path));
        Assert.NotNull(message);
        Assert.Contains("In folder is not a full path", message);

        Assert.Contains("Out folder is not a full path", FolderDialog.Validate(Folder(outFolder: path)));
        Assert.Contains("Error folder is not a full path", FolderDialog.Validate(Folder(error: path)));
        Assert.Contains("Originals folder is not a full path", FolderDialog.Validate(Folder(originals: path)));
    }

    [Fact]
    public void EmptyPath_IsStillAskedFor()
    {
        Assert.Equal("Choose the In folder.", FolderDialog.Validate(Folder(inFolder: "")));
        Assert.Equal("Choose the Error folder.", FolderDialog.Validate(Folder(error: "  ")));
    }

    [Fact]
    public void PathWithNul_IsRejected_NotThrown()
    {
        var message = FolderDialog.Validate(Folder(inFolder: "C:\\A\0\\In"));

        Assert.NotNull(message);
        Assert.Contains("not a full path", message);
    }

    [Fact]
    public void FullAndUncPaths_AreAccepted()
    {
        Assert.Null(FolderDialog.Validate(Folder(@"\\srv\share\In", @"\\srv\share\Out", @"\\srv\share\Error", @"\\srv\share\Originals")));
        Assert.Null(FolderDialog.Validate(Folder(@"D:\x\In\", @"D:\x\Out\", @"D:\x\Error\")));
    }

    [Fact]
    public void RelativeIsReportedBeforeDuplicates()
    {
        Assert.Contains("not a full path", FolderDialog.Validate(Folder(inFolder: "x", outFolder: "x")));
    }
}

public sealed class ServiceAccountDecisionTests
{
    [Theory]
    [InlineData("LocalSystem", true)]
    [InlineData("localsystem", true)]
    [InlineData(@"NT AUTHORITY\NetworkService", true)]
    [InlineData(@"nt authority\localservice", true)]
    [InlineData(@".\bob", false)]
    [InlineData(@"DOM\bob", false)]
    [InlineData(@"DOM\gmsa$", false)]
    public void IsBuiltIn(string account, bool builtIn) => Assert.Equal(builtIn, ServiceControl.IsBuiltInAccount(account));

    [Theory]
    [InlineData("LocalSystem", false, false)]
    [InlineData(@"NT AUTHORITY\NetworkService", false, false)]
    [InlineData(@"NT AUTHORITY\LocalService", false, false)]
    [InlineData(@".\bob", true, true)]
    [InlineData(@"DOM\bob", true, true)]
    [InlineData(@"bob@example.com", true, true)]
    [InlineData(@"DOM\gmsa$", false, true)] // managed service account: no password, but it still needs the right
    public void PasswordAndLogonRight(string account, bool needsPassword, bool needsRight)
    {
        Assert.Equal(needsPassword, ServiceControl.NeedsPassword(account));
        Assert.Equal(needsRight, ServiceControl.NeedsLogonRight(account));
    }

    [Theory]
    [InlineData("localsystem", "LocalSystem")]
    [InlineData("LocalSystem", "LocalSystem")]
    [InlineData("gmsa$", @".\gmsa$")]
    public void TypedBuiltInNamesAreNotMadeLocal(string typed, string expected) =>
        Assert.Equal(expected, ServiceControl.BuildAccountName(typed));
}

public sealed class ElevationTests
{
    [Fact]
    public void WindowStartedWithoutElevation_ElevatesItself()
    {
        Assert.True(Elevation.ShouldSelfElevate([], isElevated: false));
        Assert.True(Elevation.ShouldSelfElevate(["something"], isElevated: false));
    }

    [Fact]
    public void AlreadyElevated_DoesNot()
    {
        Assert.False(Elevation.ShouldSelfElevate([], isElevated: true));
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--TRAY")]
    [InlineData("--service")]
    public void TrayAndServiceCopiesNeverSelfElevate(string argument)
    {
        Assert.False(Elevation.ShouldSelfElevate([argument], isElevated: false));
        Assert.False(Elevation.ShouldSelfElevate([argument, "start"], isElevated: false));
    }
}

public sealed class PendingRequestTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExpiresAfterTheTimeout()
    {
        var pending = new PendingRequest(Start);

        Assert.False(pending.IsExpired(Start.AddSeconds(59)));
        Assert.True(pending.IsExpired(Start.AddSeconds(60)));
        Assert.True(new PendingRequest(Start, TimeSpan.FromSeconds(5)).IsExpired(Start.AddSeconds(5)));
    }

    [Fact]
    public void AnsweredOnlyByACheckAfterTheRequest()
    {
        var pending = new PendingRequest(Start);

        Assert.False(pending.IsAnsweredByCheckAt(null));
        Assert.False(pending.IsAnsweredByCheckAt(Start.AddMinutes(-5)));
        Assert.True(pending.IsAnsweredByCheckAt(Start.AddSeconds(3)));
    }
}

public sealed class FolderMatchTests
{
    [Fact]
    public void FindsByNameAndInFolder_NotByPosition()
    {
        List<WatchedFolder> folders =
        [
            new() { Name = "B", In = @"C:\b\In" },
            new() { Name = "A", In = @"C:\a\In" },
            new() { Name = "A", In = @"C:\other\In" },
        ];

        Assert.Equal(1, FolderMatch.IndexOf(folders, "a", @"C:\A\IN"));
        Assert.Equal(2, FolderMatch.IndexOf(folders, "A", @"C:\other\In"));
        Assert.Equal(-1, FolderMatch.IndexOf(folders, "A", @"C:\gone\In"));
        Assert.Equal(-1, FolderMatch.IndexOf([], "A", @"C:\a\In"));
    }
}

public sealed class UpdateViewServiceTests
{
    private static readonly Version Installed = new(1, 0, 0);

    [Fact]
    public void ServiceNotRunning_DisablesUpdateNowAndSaysWhy()
    {
        var view = UpdateView.From(new UpdateState { AvailableVersion = "1.2.0" }, Installed, serviceRunning: false);

        Assert.True(view.ShowBanner);
        Assert.False(view.CanUpdate);
        Assert.Equal("The service is not running. Start it to update.", view.PhaseText);
    }

    [Fact]
    public void ServiceRunning_EnablesUpdateNow()
    {
        Assert.True(UpdateView.From(new UpdateState { AvailableVersion = "1.2.0" }, Installed, serviceRunning: true).CanUpdate);
    }
}

public sealed class TrayBusyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-tray2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(UpdatePhase.Downloading)]
    [InlineData(UpdatePhase.WaitingForJobs)]
    [InlineData(UpdatePhase.Installing)]
    public void NoPopupWhileAnUpdateIsUnderWay(string phase)
    {
        var memory = new TrayMemory(Path.Combine(_dir, "tray.json"));
        var state = new UpdateState { AvailableVersion = "1.2.0", Phase = phase };

        Assert.Null(TrayPolicy.VersionToAnnounce(state, new Version(1, 0, 0), memory, null));
    }

    [Fact]
    public void PopupAfterAFailedUpdate()
    {
        var memory = new TrayMemory(Path.Combine(_dir, "tray.json"));
        var state = new UpdateState { AvailableVersion = "1.2.0", Phase = UpdatePhase.Failed };

        Assert.Equal("1.2.0", TrayPolicy.VersionToAnnounce(state, new Version(1, 0, 0), memory, null));
    }
}

[Collection("AppRoot")]
public sealed class SettingsLogTests : AppRootTestBase
{
    [Fact]
    public void WritesToTheDatedLogFileUnderLogs()
    {
        SettingsLog.Write("hello", new InvalidOperationException("boom"));

        var file = SettingsLog.FileFor(DateTime.Now);
        Assert.StartsWith(AppPaths.LogsDir, file);
        Assert.Matches(@"settings-\d{8}\.log$", file);
        var text = File.ReadAllText(file);
        Assert.Contains("hello", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void NeverThrows_WhenTheLogFolderCannotBeCreated()
    {
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(AppPaths.LogsDir, "a file where the folder should be");

        SettingsLog.Write("ignored");
    }
}
