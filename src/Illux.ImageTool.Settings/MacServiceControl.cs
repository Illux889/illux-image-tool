using System.Diagnostics;
using System.Runtime.Versioning;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The watcher daemon from the settings window on macOS. Reading the status needs no rights (<c>launchctl print</c>);
/// start and stop run <c>launchctl</c> through <c>osascript ... with administrator privileges</c>, the standard macOS
/// administrator prompt. Every command is a fixed string passed as one argument (never a shell line built from input).
/// There is no service account to change on macOS.
/// </summary>
internal static class MacServiceControl
{
    public const string DaemonLabel = "dk.illux.imagetool.watcher";

    /// <summary>The daemon's launchd job, installed by the macOS package.</summary>
    public const string DaemonPlist = "/Library/LaunchDaemons/" + DaemonLabel + ".plist";

    private const string OsaScript = "/usr/bin/osascript";
    private const string LaunchCtl = "/bin/launchctl";

    /// <summary>osascript's error number when the user cancels the administrator prompt.</summary>
    private const string UserCancelled = "(-128)";

    /// <summary><c>osascript -e 'do shell script "/bin/launchctl kickstart -k|bootout system/…" with administrator privileges'</c>.</summary>
    public static ProcessStartInfo BuildStartInfo(ServiceAction action)
    {
        var command = action switch
        {
            ServiceAction.Start => $"{LaunchCtl} kickstart -k system/{DaemonLabel}",
            ServiceAction.Stop => $"{LaunchCtl} bootout system/{DaemonLabel}",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "There is no service account to change on macOS."),
        };
        return Build(OsaScript, "-e", $"do shell script \"{command}\" with administrator privileges");
    }

    /// <summary><c>launchctl print system/dk.illux.imagetool.watcher</c>: readable without administrator rights.</summary>
    public static ProcessStartInfo BuildStatusStartInfo() => Build(LaunchCtl, "print", $"system/{DaemonLabel}");

    /// <summary>
    /// The status from <c>launchctl print</c>: its <c>state = …</c> line when the job is loaded; when it is not, the
    /// daemon was stopped (booted out) if its plist is installed, otherwise it is not installed.
    /// </summary>
    public static string DescribeStatus(int exitCode, string output, bool plistInstalled)
    {
        if (exitCode != 0) return plistInstalled ? "Stopped" : "Not installed";
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("state = ", StringComparison.Ordinal)) continue;
            return trimmed["state = ".Length..].Trim() == "running" ? "Running" : "Stopped";
        }
        return "Unknown";
    }

    /// <summary>True when osascript failed because the user cancelled the administrator prompt (nothing to report).</summary>
    public static bool IsCancelled(int exitCode, string error) => exitCode != 0 && error.Contains(UserCancelled, StringComparison.Ordinal);

    private static ProcessStartInfo Build(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>"Running", "Stopped", "Not installed" or "Unknown"; never throws. Waits for launchctl: not on the UI thread.</summary>
    [SupportedOSPlatform("macos")]
    public static string GetStatusText()
    {
        var result = RunProcess(BuildStatusStartInfo(), TimeSpan.FromSeconds(5));
        return result is { } r ? DescribeStatus(r.ExitCode, r.Output, File.Exists(DaemonPlist)) : "Unknown";
    }

    /// <summary>
    /// Starts or stops the daemon behind the macOS administrator prompt; waits until it is answered and done. Null when
    /// it worked or the user cancelled the prompt, otherwise the message to show.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static string? Run(ServiceAction action)
    {
        var result = RunProcess(BuildStartInfo(action), Timeout.InfiniteTimeSpan);
        if (result is not { } r) return "Could not start the action.";
        if (r.ExitCode == 0 || IsCancelled(r.ExitCode, r.Error)) return null;
        var detail = r.Error.Trim();
        return "Could not start the action." + (detail.Length > 0 ? "\n" + detail : "");
    }

    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);

    /// <summary>Runs the process and collects its output; null when it could not be started or did not end in time.</summary>
    private static ProcessResult? RunProcess(ProcessStartInfo info, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return new ProcessResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
