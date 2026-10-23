using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Tests;

[Collection("AppRoot")]
public sealed class RunLockTests : AppRootTestBase
{
    [Fact]
    public void RunLock_ActiveWhileHeld_InactiveAfterDispose()
    {
        Assert.False(RunLock.AnyActive());

        var held = RunLock.Acquire();
        Assert.True(RunLock.AnyActive());
        Assert.Single(Directory.GetFiles(AppPaths.RunsDir, "*.lock"));

        held.Dispose();
        Assert.False(RunLock.AnyActive());
        Assert.Empty(Directory.GetFiles(AppPaths.RunsDir, "*.lock"));
    }

    [Fact]
    public void RunLock_TwoHeld_ActiveUntilBothDisposed()
    {
        var a = RunLock.Acquire();
        var b = RunLock.Acquire();

        a.Dispose();
        Assert.True(RunLock.AnyActive());

        b.Dispose();
        Assert.False(RunLock.AnyActive());
    }

    private static string Stale()
    {
        Directory.CreateDirectory(AppPaths.RunsDir);
        var stale = Path.Combine(AppPaths.RunsDir, "4242-" + Guid.NewGuid().ToString("N") + ".lock");
        File.WriteAllText(stale, "left behind by a crashed run");
        return stale;
    }

    [Fact]
    public void AnyActive_StaleFile_IsIgnoredButNeverDeleted()
    {
        var stale = Stale();

        Assert.False(RunLock.AnyActive());
        Assert.True(File.Exists(stale)); // the privileged caller only reads (R39)
        Assert.Equal("left behind by a crashed run", File.ReadAllText(stale));
    }

    [Fact]
    public void Acquire_RemovesStaleFiles_ButNotLiveLocks()
    {
        var stale = Stale();
        using var live = RunLock.Acquire();

        using var second = RunLock.Acquire();

        Assert.False(File.Exists(stale));
        Assert.Equal(2, Directory.GetFiles(AppPaths.RunsDir, "*.lock").Length);
    }

    [Fact]
    public void AnyActive_NoRunsFolder_False()
    {
        Assert.False(Directory.Exists(AppPaths.RunsDir));
        Assert.False(RunLock.AnyActive());
    }

    [Fact]
    public void AnyActive_LockThatCannotBeRead_DoesNotBlockUpdatesForever()
    {
        var unreadable = Stale();
        var security = new FileInfo(unreadable).GetAccessControl();
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            me.User!, System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.WriteData, System.Security.AccessControl.AccessControlType.Deny));
        new FileInfo(unreadable).SetAccessControl(security);
        Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(unreadable));

        Assert.False(RunLock.AnyActive());
    }

    [FactWhenFileSymlinksWork]
    public void AnyActive_SkipsLinks_AndAcquireLeavesThemAlone()
    {
        Directory.CreateDirectory(AppPaths.RunsDir);
        var target = Path.Combine(Dir, "elsewhere.txt");
        File.WriteAllText(target, "not a lock");
        var link = Path.Combine(AppPaths.RunsDir, "1-link.lock");
        File.CreateSymbolicLink(link, target);

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(RunLock.AnyActive()); // following the link would see a sharing violation
        }
        RunLock.Acquire().Dispose();

        Assert.True(File.Exists(link));
        Assert.Equal("not a lock", File.ReadAllText(target));
    }

    [Fact]
    public void AppPaths_UseOverrideRoot()
    {
        Assert.Equal(AppRoot, AppPaths.Root);
        Assert.Equal(Path.Combine(AppRoot, "config.json"), AppPaths.ConfigFile);
        Assert.Equal(Path.Combine(AppRoot, "logs"), AppPaths.LogsDir);
        Assert.Equal(Path.Combine(AppRoot, "runs"), AppPaths.RunsDir);
        Assert.Equal(Path.Combine(AppRoot, "updating.flag"), AppPaths.UpdatingFlag);
        Assert.Equal(Path.Combine(AppRoot, "service-logs"), AppPaths.ServiceLogsDir);
    }

    [Fact]
    public void AppPaths_DefaultRootIsProgramData()
    {
        Environment.SetEnvironmentVariable("ILLUX_IMAGETOOL_ROOT", null);

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Illux Image Tool"),
            AppPaths.Root);
    }
}
