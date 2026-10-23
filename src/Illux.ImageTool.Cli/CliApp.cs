using System.Reflection;
using Illux.ImageTool.Ae;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Options;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Cli;

/// <summary>The command line: <c>ae</c> (Automation Engine), <c>run</c> (manual), <c>--version</c>, <c>--help</c>.</summary>
public static class CliApp
{
    /// <summary>Runs the command line and returns the exit code (0 = OK, 1 = Warning, 2 = Error).</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);

        try
        {
            if (args.Length == 0)
            {
                throw new UsageException("no command given. Use --help to see how to call the tool.");
            }

            switch (args[0].ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    stdout.WriteLine(CliHelp.Text);
                    return 0;
                case "--version":
                    stdout.WriteLine(Version());
                    return 0;
                case "ae":
                    return await RunAeAsync(args, stdout, ct).ConfigureAwait(false);
                case "run":
                    return await RunManualAsync(args, stdout, ct).ConfigureAwait(false);
                default:
                    throw new UsageException($"unknown command '{args[0]}'. Use --help to see how to call the tool.");
            }
        }
        catch (Exception ex) when (ex is UsageException or ParameterException)
        {
            stdout.WriteLine($"ERROR: {ex.Message}");
            return (int)Status.Error;
        }
        catch (OperationCanceledException)
        {
            stdout.WriteLine("ERROR: cancelled");
            return (int)Status.Error;
        }
        catch (Exception ex)
        {
            LogFile.Write(ex);
            try { stdout.WriteLine($"ERROR: {ex.Message}"); } catch (Exception) { /* stdout is gone; the exit code still tells AE */ }
            return (int)Status.Error;
        }
    }

    private static Task<int> RunAeAsync(string[] args, TextWriter stdout, CancellationToken ct)
    {
        if (args.Length < 3)
        {
            throw new UsageException("'ae' needs the input files and the output folder: IlluxImageTool.exe ae \"<input files>\" \"<output folder>\" [parameters].");
        }

        // First two lines of the AE task log: they show what AE really passed as %1 and %2.
        stdout.WriteLine($"Inputs: {args[1]}");
        stdout.WriteLine($"Output folder: {args[2]}");

        var inputs = InputListParser.Split(args[1]);
        var options = ParameterParser.Parse(args.Skip(3));
        return ExecuteAsync(inputs, args[2], options, stdout, ct);
    }

    private static Task<int> RunManualAsync(string[] args, TextWriter stdout, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            throw new UsageException("'run' needs a file or folder: IlluxImageTool.exe run <file-or-folder> --out <output folder> [parameters].");
        }

        string? outputFolder = null;
        var parameters = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) throw new UsageException("--out needs an output folder.");
                outputFolder = args[++i];
            }
            else
            {
                parameters.Add(args[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            throw new UsageException("'run' needs an output folder: --out <folder>.");
        }

        var options = ParameterParser.Parse(parameters);
        return ExecuteAsync(ResolveInputs(args[1]), outputFolder, options, stdout, ct);
    }

    /// <summary>A file is used as given; a folder means the files directly in it, without hidden ones.</summary>
    private static IReadOnlyList<string> ResolveInputs(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full)) return [full];
        if (!Directory.Exists(full)) throw new UsageException($"input '{path}' does not exist.");

        return new DirectoryInfo(full)
            .EnumerateFiles()
            .Where(f => (f.Attributes & FileAttributes.Hidden) == 0)
            .Select(f => f.FullName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<int> ExecuteAsync(
        IReadOnlyList<string> inputs, string outputFolder, ProcessingOptions options, TextWriter stdout, CancellationToken ct)
    {
        var (runLock, timedOut) = await AcquireLockWhenNotUpdatingAsync(stdout, ct).ConfigureAwait(false);
        using var held = runLock;
        if (timedOut)
        {
            stdout.WriteLine("ERROR: update did not finish");
            return (int)Status.Error;
        }

        MagickSetup.Configure();
        var status = await BatchRunner.RunAsync(inputs, outputFolder, options, stdout, ct).ConfigureAwait(false);
        return (int)status;
    }

    /// <summary>
    /// Takes the run lock, then checks <c>updating.flag</c>: the updater writes the flag only when no lock is held, so a
    /// lock taken just before the flag appeared must not keep a conversion running into an install. While the flag is
    /// there the lock is released and we wait (same variables and defaults as the AE scripts; seconds may be fractional).
    /// </summary>
    private static async Task<(RunLock? Lock, bool TimedOut)> AcquireLockWhenNotUpdatingAsync(TextWriter stdout, CancellationToken ct)
    {
        var step = SecondsFromEnvironment("ILLUX_IMAGETOOL_WAIT_SECONDS", 5);
        var max = SecondsFromEnvironment("ILLUX_IMAGETOOL_WAIT_MAX_SECONDS", 600, allowZero: true);
        var waited = TimeSpan.Zero;
        var announced = false;

        while (true)
        {
            var runLock = TryAcquireLock();
            if (!File.Exists(AppPaths.UpdatingFlag)) return (runLock, false);

            runLock?.Dispose();
            if (!announced)
            {
                stdout.WriteLine("Waiting for the update of Illux Image Tool to finish...");
                announced = true;
            }
            if (waited >= max) return (null, true);

            await Task.Delay(step, ct).ConfigureAwait(false);
            waited += step;
        }
    }

    private static TimeSpan SecondsFromEnvironment(string name, double fallbackSeconds, bool allowZero = false)
    {
        var text = Environment.GetEnvironmentVariable(name);
        var seconds = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                      && (parsed > 0 || (allowZero && parsed == 0)) && parsed < 86400
            ? parsed
            : fallbackSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The lock only tells the updater we are busy; a conversion must not fail because it cannot be taken.</summary>
    private static RunLock? TryAcquireLock()
    {
        try
        {
            return RunLock.Acquire();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFile.Write(ex);
            return null;
        }
    }

    private static string Version()
    {
        var informational = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? typeof(CliApp).Assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    private sealed class UsageException(string message) : Exception(message);
}
