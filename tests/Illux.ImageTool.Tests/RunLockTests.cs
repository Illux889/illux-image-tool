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

    [Fact]
    public void RunLock_StaleFileIgnored()
    {
        Directory.CreateDirectory(AppPaths.RunsDir);
        var stale = Path.Combine(AppPaths.RunsDir, "4242-" + Guid.NewGuid().ToString("N") + ".lock");
        File.WriteAllText(stale, "left behind by a crashed run");

        Assert.False(RunLock.AnyActive());
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void AppPaths_UseOverrideRoot()
    {
        Assert.Equal(AppRoot, AppPaths.Root);
        Assert.Equal(Path.Combine(AppRoot, "config.json"), AppPaths.ConfigFile);
        Assert.Equal(Path.Combine(AppRoot, "logs"), AppPaths.LogsDir);
        Assert.Equal(Path.Combine(AppRoot, "runs"), AppPaths.RunsDir);
        Assert.Equal(Path.Combine(AppRoot, "updating.flag"), AppPaths.UpdatingFlag);
        Assert.Equal(Path.Combine(AppRoot, "requests"), AppPaths.RequestsDir);
        Assert.Equal(Path.Combine(AppRoot, "requests", "check-now.flag"), AppPaths.CheckNowFlag);
        Assert.Equal(Path.Combine(AppRoot, "requests", "install-now.flag"), AppPaths.InstallNowFlag);
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
