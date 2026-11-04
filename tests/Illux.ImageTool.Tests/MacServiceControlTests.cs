using Illux.ImageTool.Settings;

namespace Illux.ImageTool.Tests;

/// <summary>The macOS service buttons: only the commands are built and checked here; nothing is run.</summary>
public sealed class MacServiceControlTests
{
    [Fact]
    public void Start_AsksForAnAdministratorAndKickstartsTheDaemon()
    {
        var info = MacServiceControl.BuildStartInfo(ServiceAction.Start);

        Assert.Equal("/usr/bin/osascript", info.FileName);
        Assert.Equal(
            ["-e", "do shell script \"/bin/launchctl kickstart -k system/dk.illux.imagetool.watcher\" with administrator privileges"],
            info.ArgumentList);
        Assert.Equal("", info.Arguments); // never one shell string
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void Stop_AsksForAnAdministratorAndBootsTheDaemonOut()
    {
        var info = MacServiceControl.BuildStartInfo(ServiceAction.Stop);

        Assert.Equal("/usr/bin/osascript", info.FileName);
        Assert.Equal(
            ["-e", "do shell script \"/bin/launchctl bootout system/dk.illux.imagetool.watcher\" with administrator privileges"],
            info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void NoAccountChangeOnMac() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MacServiceControl.BuildStartInfo(ServiceAction.Account));

    [Fact]
    public void Status_IsReadWithLaunchctlPrint_NoAdministratorNeeded()
    {
        var info = MacServiceControl.BuildStatusStartInfo();

        Assert.Equal("/bin/launchctl", info.FileName);
        Assert.Equal(["print", "system/dk.illux.imagetool.watcher"], info.ArgumentList);
        Assert.Equal("", info.Arguments);
        Assert.False(info.UseShellExecute);
    }

    [Theory]
    [InlineData(0, "system/dk.illux.imagetool.watcher = {\n\tactive count = 1\n\tstate = running\n\tprogram = /x\n}", true, "Running")]
    [InlineData(0, "system/dk.illux.imagetool.watcher = {\n\tstate = not running\n}", true, "Stopped")]
    [InlineData(0, "system/dk.illux.imagetool.watcher = {\n\tstate = waiting\n}", true, "Stopped")]
    [InlineData(113, "Could not find service \"dk.illux.imagetool.watcher\" in domain for system", true, "Stopped")]
    [InlineData(113, "Could not find service \"dk.illux.imagetool.watcher\" in domain for system", false, "Not installed")]
    [InlineData(0, "no state line at all", true, "Unknown")]
    public void Status_FromLaunchctlPrint(int exitCode, string output, bool plistInstalled, string expected) =>
        Assert.Equal(expected, MacServiceControl.DescribeStatus(exitCode, output, plistInstalled));

    [Theory]
    [InlineData(1, "0:76: execution error: User canceled. (-128)", true)]
    [InlineData(1, "0:120: execution error: Could not find service in domain for system (113)", false)]
    [InlineData(0, "", false)]
    public void Cancelled_IsTheUserDecliningThePrompt(int exitCode, string error, bool cancelled) =>
        Assert.Equal(cancelled, MacServiceControl.IsCancelled(exitCode, error));
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
