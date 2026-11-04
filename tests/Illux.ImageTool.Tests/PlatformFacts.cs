namespace Illux.ImageTool.Tests;

/// <summary>A test that depends on Windows (ACLs, junctions, named events, AE .bat/.ps1 scripts, LSA, the service
/// control manager); reported as skipped on other systems.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = PlatformSkip.WindowsOnly;
    }
}

/// <summary>The <see cref="TheoryAttribute"/> form of <see cref="WindowsFactAttribute"/>.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = PlatformSkip.WindowsOnly;
    }
}

/// <summary>A test of macOS-only behaviour; reported as skipped on other systems.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = PlatformSkip.MacOnly;
    }
}

/// <summary>macOS only, and skipped when the tests run as root: such a test needs a folder that root does not own.</summary>
public sealed class MacFactWhenNotRootAttribute : FactAttribute
{
    public MacFactWhenNotRootAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = PlatformSkip.MacOnly;
        else if (Illux.ImageTool.Runtime.UnixNative.IsSupported && Illux.ImageTool.Runtime.UnixNative.EffectiveUserId == 0)
            Skip = "Runs only as an ordinary user: as root every folder the test creates is owned by root.";
    }
}

internal static class PlatformSkip
{
    public const string WindowsOnly = "Windows only: tests Windows-specific behaviour (ACLs, junctions, named events, AE scripts, services).";
    public const string MacOnly = "macOS only: tests macOS-specific behaviour.";
}
