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

internal static class PlatformSkip
{
    public const string WindowsOnly = "Windows only: tests Windows-specific behaviour (ACLs, junctions, named events, AE scripts, services).";
    public const string MacOnly = "macOS only: tests macOS-specific behaviour.";
}
