using System.Diagnostics;
using System.Security.Principal;

namespace Illux.ImageTool.Tests;

/// <summary>Skipped (and reported as skipped) when the test runner is elevated: folders it creates are then owned by Administrators.</summary>
public sealed class FactWhenNotElevatedAttribute : FactAttribute
{
    public FactWhenNotElevatedAttribute()
    {
        if (OperatingSystem.IsWindows() && IsElevated())
            Skip = "Runs only for a non-elevated user (an elevated user's folders are owned by Administrators, which is trusted).";
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Skipped when a junction cannot be created with <c>mklink /J</c> on this machine.</summary>
public sealed class FactWhenJunctionsWorkAttribute : FactAttribute
{
    private static readonly Lazy<bool> Works = new(Probe);

    public FactWhenJunctionsWorkAttribute()
    {
        if (!Works.Value) Skip = "Junctions cannot be created here (mklink /J failed).";
    }

    public static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static bool Probe()
    {
        var root = Path.Combine(Path.GetTempPath(), "illux-junction-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "target"));
            return TryCreateJunction(Path.Combine(root, "link"), Path.Combine(root, "target"));
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
