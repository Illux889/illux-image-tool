using Illux.ImageTool.Settings;

namespace Illux.ImageTool.Tests;

/// <summary>The macOS service buttons: only the commands are built and checked here; nothing is run.</summary>
public sealed class MacServiceControlTests
{
    [Fact]
    public void Start_AsksForAnAdministratorOnce_LoadsBothJobsIfNeeded_AndKickstartsThem()
    {
        var info = MacServiceControl.BuildStartInfo(ServiceAction.Start);

        // V9: after a Stop (bootout) a job is unloaded, so Start loads it again first; "already loaded" is ignored and
        // kickstart decides the outcome. V15: the folders job too, when the package set it up (its plist exists). One
        // administrator prompt for all of it.
        Assert.Equal("/usr/bin/osascript", info.FileName);
        Assert.Equal(
            ["-e", "do shell script \"/bin/launchctl bootstrap system /Library/LaunchDaemons/dk.illux.imagetool.watcher.plist 2>/dev/null; "
                + "/bin/launchctl kickstart -k system/dk.illux.imagetool.watcher && "
                + "if [ -f /Library/LaunchDaemons/dk.illux.imagetool.folders.plist ]; then "
                + "/bin/launchctl bootstrap system /Library/LaunchDaemons/dk.illux.imagetool.folders.plist 2>/dev/null; "
                + "/bin/launchctl kickstart -k system/dk.illux.imagetool.folders; fi\" with administrator privileges"],
            info.ArgumentList);
        Assert.Equal("", info.Arguments); // never one shell string
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void Stop_AsksForAnAdministratorOnce_AndBootsBothJobsOut()
    {
        var info = MacServiceControl.BuildStartInfo(ServiceAction.Stop);

        // Either job may already be stopped; what counts is that neither is loaded afterwards.
        Assert.Equal("/usr/bin/osascript", info.FileName);
        Assert.Equal(
            ["-e", "do shell script \"/bin/launchctl bootout system/dk.illux.imagetool.folders 2>/dev/null; "
                + "/bin/launchctl bootout system/dk.illux.imagetool.watcher 2>/dev/null; "
                + "if /bin/launchctl print system/dk.illux.imagetool.watcher >/dev/null 2>&1 || "
                + "/bin/launchctl print system/dk.illux.imagetool.folders >/dev/null 2>&1; "
                + "then echo 'The watcher service is still loaded.' >&2; exit 1; fi\" with administrator privileges"],
            info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void NoAccountChangeOnMac() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MacServiceControl.BuildStartInfo(ServiceAction.Account));

    [Theory]
    [InlineData("dk.illux.imagetool.watcher")]
    [InlineData("dk.illux.imagetool.folders")]
    public void Status_OfBothJobs_IsReadWithLaunchctlPrint_NoAdministratorNeeded(string label)
    {
        var info = MacServiceControl.BuildStatusStartInfo(label);

        Assert.Equal("/bin/launchctl", info.FileName);
        Assert.Equal(["print", "system/" + label], info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
    }

    [Theory]
    [InlineData("Running", "Running", "Running")]
    [InlineData("Stopped", "Stopped", "Stopped")]
    [InlineData("Running", "Not installed", "Running")] // no folder account: only the root job is set up
    [InlineData("Stopped", "Not installed", "Stopped")]
    [InlineData("Not installed", "Not installed", "Not installed")]
    [InlineData("Running", "Stopped", "Partly running")]
    [InlineData("Stopped", "Running", "Partly running")]
    [InlineData("Running", "Unknown", "Unknown")]
    [InlineData("Unknown", "Running", "Unknown")]
    public void Status_OfTheTwoJobs_IsOneLine(string watcher, string folders, string expected) =>
        Assert.Equal(expected, MacServiceControl.CombineStatus(watcher, folders));

    [Theory]
    [InlineData("anna\n", "Watched folders run as: anna")]
    [InlineData("print_shop.2", "Watched folders run as: print_shop.2")]
    [InlineData(null, MacServiceControl.FoldersOffText)]
    [InlineData("", MacServiceControl.FoldersOffText)]
    [InlineData("root;rm\n", MacServiceControl.FoldersOffText)]
    [InlineData("Anna\n", MacServiceControl.FoldersOffText)]
    public void FolderAccount_OneLineInTheWindow(string? fileContent, string expected) =>
        Assert.Equal(expected, MacServiceControl.FolderAccountText(fileContent));

    [Fact]
    public void FolderAccount_Missing_SaysHowToTurnThemOn() =>
        Assert.Equal(
            "Watched folders are off: install the package while logged in as the account that should process them.",
            MacServiceControl.FoldersOffText);

    [Theory]
    [InlineData(0, "system/dk.illux.imagetool.watcher = {\n\tactive count = 1\n\tstate = running\n\tprogram = /x\n}", true, "Running")]
    [InlineData(0, "system/dk.illux.imagetool.watcher = {\n\tstate = not running\n}", true, "Stopped")]
    [InlineData(0, "system/dk.illux.imagetool.watcher = {\n\tstate = waiting\n}", true, "Stopped")]
    [InlineData(113, "Could not find service \"dk.illux.imagetool.watcher\" in domain for system", true, "Stopped")]
    [InlineData(113, "Could not find service \"dk.illux.imagetool.watcher\" in domain for system", false, "Not installed")]
    [InlineData(0, "no state line at all", true, "Unknown")]
    [InlineData(1, "Unrecognized target specifier.", true, "Unknown")] // only 113 means "not loaded"
    [InlineData(5, "", false, "Unknown")]
    public void Status_FromLaunchctlPrint(int exitCode, string output, bool plistInstalled, string expected) =>
        Assert.Equal(expected, MacServiceControl.DescribeStatus(exitCode, output, plistInstalled));

    [Theory]
    [InlineData(1, "0:76: execution error: User canceled. (-128)", true)]
    [InlineData(1, "0:120: execution error: Could not find service in domain for system (113)", false)]
    [InlineData(0, "", false)]
    public void Cancelled_IsTheUserDecliningThePrompt(int exitCode, string error, bool cancelled) =>
        Assert.Equal(cancelled, MacServiceControl.IsCancelled(exitCode, error));
}

/// <summary>Final review M1: the settings app's own log.</summary>
public sealed class SettingsLogFolderTests
{
    [Fact]
    public void Mac_LogsIntoTheUsersLibrary_NotIntoTheSharedLogsFolder() =>
        Assert.Equal(
            Path.Combine("/Users/anna", "Library", "Logs", "Illux Image Tool"),
            SettingsLog.DirectoryFor(elevated: false, mac: true, home: "/Users/anna"));

    [Fact]
    public void Windows_IsUnchanged()
    {
        Assert.Equal(Illux.ImageTool.Runtime.AppPaths.LogsDir, SettingsLog.DirectoryFor(elevated: false, mac: false, home: @"C:\Users\anna"));
        Assert.Equal(Illux.ImageTool.Runtime.AppPaths.ServiceLogsDir, SettingsLog.DirectoryFor(elevated: true, mac: false, home: @"C:\Users\anna"));
    }
}

public sealed class MacAdminGroupTests
{
    [Fact]
    public void Member_ByPrimaryOrSupplementaryGroup()
    {
        Assert.True(MacAdminGroup.IsMember([20, 12, 80], effectiveGroup: 20, adminGroup: 80));
        Assert.True(MacAdminGroup.IsMember([], effectiveGroup: 80, adminGroup: 80));
        Assert.False(MacAdminGroup.IsMember([20, 12, 61], effectiveGroup: 20, adminGroup: 80));
    }

    [Fact]
    public void NoAdminGroupOnTheSystem_NotAMember() =>
        Assert.False(MacAdminGroup.IsMember([20, 80], effectiveGroup: 20, adminGroup: null));

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void CurrentUser_AgreesWithId()
    {
        var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/id") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-Gn");
        using var id = System.Diagnostics.Process.Start(info)!;
        var groups = id.StandardOutput.ReadToEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        id.WaitForExit();

        Assert.Equal(groups.Contains("admin"), MacAdminGroup.CurrentUserIsMember());
    }
}
