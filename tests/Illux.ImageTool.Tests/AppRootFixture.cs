namespace Illux.ImageTool.Tests;

/// <summary>Points <c>ILLUX_IMAGETOOL_ROOT</c> at a private temp folder for the lifetime of a test class instance.</summary>
public abstract class AppRootTestBase : IDisposable
{
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable("ILLUX_IMAGETOOL_ROOT");

    protected string Dir { get; } = Path.Combine(Path.GetTempPath(), "illux-cli-" + Guid.NewGuid().ToString("N"));

    protected string AppRoot => Path.Combine(Dir, "root");

    protected AppRootTestBase()
    {
        Environment.SetEnvironmentVariable("ILLUX_IMAGETOOL_ROOT", AppRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ILLUX_IMAGETOOL_ROOT", _previousRoot);
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The environment variable is process-wide, so classes that set it must not run in parallel.</summary>
[CollectionDefinition("AppRoot", DisableParallelization = true)]
public sealed class AppRootCollection;
