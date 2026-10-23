using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Proves <see cref="TestProcessGuard"/> is active: outside any per-test override the process (and so every child process)
/// must resolve the app root to a temp folder, never to the real ProgramData folder.
/// Not derived from <see cref="AppRootTestBase"/> on purpose, because that would apply a per-test override first.
/// </summary>
[Collection("AppRoot")]
public sealed class TestProcessGuardTests
{
    private static string AsFolderPrefix(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    [Fact]
    public void AppPathsRoot_IsNotUnderCommonApplicationData()
    {
        var programData = AsFolderPrefix(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var root = AsFolderPrefix(AppPaths.Root);

        Assert.False(
            root.StartsWith(programData, StringComparison.OrdinalIgnoreCase),
            $"the test process resolves the app root to '{root}', inside the real ProgramData folder");
    }

    [Fact]
    public void AppPathsRoot_IsAFolderUnderTheTempPath()
    {
        Assert.StartsWith(AsFolderPrefix(Path.GetTempPath()), AsFolderPrefix(AppPaths.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootVariable_IsSetInTheProcessItself_SoChildProcessesInheritIt()
    {
        var variable = Environment.GetEnvironmentVariable(AppPaths.RootOverrideVariable);

        Assert.False(string.IsNullOrWhiteSpace(variable));
        Assert.Equal(AppPaths.Root, variable);
    }
}
