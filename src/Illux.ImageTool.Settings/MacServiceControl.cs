using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The watcher daemon from the settings window on macOS: two launchd jobs since V15, the root job (control socket and
/// updates) and the folders job (watched folders, run as the folder account). Reading the status needs no rights
/// (<c>launchctl print</c>); start and stop run <c>launchctl</c> through <c>osascript ... with administrator
/// privileges</c>, the standard macOS administrator prompt, once for both jobs. Every command is a fixed string passed
/// as one argument (never a shell line built from input). There is no service account to change on macOS.
/// </summary>
internal static partial class MacServiceControl
{
    public const string DaemonLabel = "dk.illux.imagetool.watcher";

    /// <summary>V15: the job that runs the watched folders as the folder account.</summary>
    public const string FoldersLabel = "dk.illux.imagetool.folders";

    /// <summary>The root job's launchd plist, installed by the macOS package.</summary>
    public const string DaemonPlist = "/Library/LaunchDaemons/" + DaemonLabel + ".plist";

    /// <summary>The folders job's plist, written by the package's postinstall only when there is a folder account.</summary>
    public const string FoldersPlist = "/Library/LaunchDaemons/" + FoldersLabel + ".plist";

    /// <summary>Shown when <c>folder-account</c> is missing (nobody was logged in at the first install).</summary>
    public const string FoldersOffText = "Watched folders are off: install the package while logged in as the account that should process them.";

    private const string OsaScript = "/usr/bin/osascript";
    private const string LaunchCtl = "/bin/launchctl";

    /// <summary>launchctl's exit code for "Could not find service … in domain": the job is not loaded.</summary>
    private const int NotLoaded = 113;

    /// <summary>osascript's error number when the user cancels the administrator prompt.</summary>
    private const string UserCancelled = "(-128)";

    /// <summary>
    /// <c>osascript -e 'do shell script "…" with administrator privileges'</c>: one administrator prompt. Start runs
    /// <c>launchctl bootstrap</c> (the plist) then <c>launchctl kickstart -k</c> for the root job, then the same for the
    /// folders job when its plist exists; Stop runs <c>launchctl bootout</c> for both and fails only when one is still
    /// loaded afterwards.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(ServiceAction action)
    {
        var command = action switch
        {
            // V9: Stop boots the jobs out, so Start loads them again first ("already loaded" and other bootstrap errors
            // are ignored; kickstart then decides the outcome and reports its own error).
            ServiceAction.Start =>
                $"{LaunchCtl} bootstrap system {DaemonPlist} 2>/dev/null; {LaunchCtl} kickstart -k system/{DaemonLabel} && "
                + $"if [ -f {FoldersPlist} ]; then {LaunchCtl} bootstrap system {FoldersPlist} 2>/dev/null; "
                + $"{LaunchCtl} kickstart -k system/{FoldersLabel}; fi",
            ServiceAction.Stop =>
                $"{LaunchCtl} bootout system/{FoldersLabel} 2>/dev/null; {LaunchCtl} bootout system/{DaemonLabel} 2>/dev/null; "
                + $"if {LaunchCtl} print system/{DaemonLabel} >/dev/null 2>&1 || {LaunchCtl} print system/{FoldersLabel} >/dev/null 2>&1; "
                + "then echo 'The watcher service is still loaded.' >&2; exit 1; fi",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "There is no service account to change on macOS."),
        };
        return Build(OsaScript, "-e", $"do shell script \"{command}\" with administrator privileges");
    }

    /// <summary><c>launchctl print system/&lt;label&gt;</c>: readable without administrator rights.</summary>
    public static ProcessStartInfo BuildStatusStartInfo(string label) => Build(LaunchCtl, "print", $"system/{label}");

    /// <summary>
    /// The status of one job from <c>launchctl print</c>: its <c>state = …</c> line when the job is loaded; when it is not
    /// loaded (exit 113), the job was stopped (booted out) if its plist is installed, otherwise it is not installed. Any
    /// other failure is "Unknown".
    /// </summary>
    public static string DescribeStatus(int exitCode, string output, bool plistInstalled)
    {
        if (exitCode == NotLoaded) return plistInstalled ? "Stopped" : "Not installed";
        if (exitCode != 0) return "Unknown";
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("state = ", StringComparison.Ordinal)) continue;
            return trimmed["state = ".Length..].Trim() == "running" ? "Running" : "Stopped";
        }
        return "Unknown";
    }

    /// <summary>
    /// One line for both jobs. A folders job that is not installed (no folder account; the window says so in its own
    /// line) leaves the root job's status; otherwise the same status for both, "Unknown" when either cannot be told,
    /// and "Partly running" when one runs and the other does not (both buttons then stay usable).
    /// </summary>
    public static string CombineStatus(string watcher, string folders)
    {
        if (folders == "Not installed" || watcher == folders) return watcher;
        if (watcher == "Unknown" || folders == "Unknown") return "Unknown";
        return "Partly running";
    }

    /// <summary>
    /// The window's line about the folder account: "Watched folders run as: &lt;account&gt;" for the first line of
    /// <c>folder-account</c> when it is a valid account name (as the package checks it), otherwise <see cref="FoldersOffText"/>.
    /// </summary>
    public static string FolderAccountText(string? fileContent)
    {
        var name = (fileContent ?? "").Split('\n')[0].TrimEnd('\r');
        return AccountName().IsMatch(name) ? $"Watched folders run as: {name}" : FoldersOffText;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_.-]{0,31}$")]
    private static partial Regex AccountName();

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

    /// <summary>
    /// "Running", "Stopped", "Partly running", "Not installed" or "Unknown" for both jobs; never throws. Waits for
    /// launchctl: not on the UI thread.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static string GetStatusText() =>
        CombineStatus(JobStatus(DaemonLabel, DaemonPlist), JobStatus(FoldersLabel, FoldersPlist));

    [SupportedOSPlatform("macos")]
    private static string JobStatus(string label, string plist)
    {
        var result = RunProcess(BuildStatusStartInfo(label), TimeSpan.FromSeconds(5));
        return result is { } r ? DescribeStatus(r.ExitCode, r.Output, File.Exists(plist)) : "Unknown";
    }

    /// <summary>The folder account line from <c>folder-account</c> (readable by everyone); never throws.</summary>
    [SupportedOSPlatform("macos")]
    public static string ReadFolderAccountText()
    {
        try
        {
            return FolderAccountText(File.Exists(AppPaths.FolderAccountFile) ? File.ReadAllText(AppPaths.FolderAccountFile) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FolderAccountText(null);
        }
    }

    /// <summary>
    /// Starts or stops both jobs behind the macOS administrator prompt; waits until it is answered and done. Null when
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
