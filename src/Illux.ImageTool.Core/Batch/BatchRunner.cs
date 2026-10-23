using Illux.ImageTool.Options;

namespace Illux.ImageTool.Batch;

/// <summary>Runs all inputs of one AE task and writes the log (spec 6): one line per file, then <c>Result: …</c>.</summary>
public static class BatchRunner
{
    /// <summary>Returns the worst status of all files; an empty input list is a Warning.</summary>
    public static async Task<Status> RunAsync(
        IReadOnlyList<string> inputs, string outputFolder, ProcessingOptions options, TextWriter log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var worst = Status.Ok;
        if (inputs.Count == 0)
        {
            log.WriteLine("WARNING: No input files");
            worst = Status.Warning;
        }
        else
        {
            var folderError = EnsureFolder(outputFolder);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in inputs)
            {
                ct.ThrowIfCancellationRequested();
                var result = folderError is null
                    ? await FileProcessor.ProcessAsync(input, outputFolder, options, NamePolicy.Overwrite, claimed, ct).ConfigureAwait(false)
                    : new FileResult(input, Status.Error, [folderError], []);
                log.WriteLine(Line(result));
                worst = worst.Worst(result.Status);
            }
        }

        log.WriteLine($"Result: {(worst == Status.Ok ? "OK" : worst.ToString())} ({inputs.Count} files)");
        return worst;
    }

    /// <summary>Creates the output folder if needed (ruling R2); returns the reason when that is impossible.</summary>
    private static string? EnsureFolder(string outputFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputFolder)) return "no output folder given";
            Directory.CreateDirectory(outputFolder);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"cannot create output folder '{outputFolder}': {ex.Message}";
        }
    }

    private static string Line(FileResult result)
    {
        var prefix = result.Status switch { Status.Error => "ERROR: ", Status.Warning => "WARNING: ", _ => "" };
        var messages = result.Messages.Count == 0 ? "OK" : string.Join("; ", result.Messages);
        return $"{prefix}{Path.GetFileName(result.Input)}: {messages}";
    }
}
