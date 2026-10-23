using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Options;
using Illux.ImageTool.Pdf;

namespace Illux.ImageTool.Batch;

public enum NamePolicy
{
    /// <summary>An existing file of the same name is replaced (AE re-runs, spec 6).</summary>
    Overwrite,

    /// <summary>An existing file is kept; the output becomes <c>name (2).ext</c>, <c>name (3).ext</c>, …</summary>
    Suffix,
}

/// <param name="Outputs">Final paths of the files written (empty for an Error).</param>
public sealed record FileResult(string Input, Status Status, IReadOnlyList<string> Messages, IReadOnlyList<string> Outputs);

/// <summary>
/// Processes one input file into the output folder following the AE output contract (spec 6): the output(s) are
/// produced in a temporary folder next to the destination and moved into place only when complete, so a failing file
/// leaves nothing behind.
/// </summary>
public static class FileProcessor
{
    private const string TempPrefix = ".illux-tmp-";

    /// <param name="claimedNames">
    /// File names already written in this batch (compared as given; use a case-insensitive set). A target name in it is
    /// an Error; names written by this call are added on success.
    /// </param>
    public static async Task<FileResult> ProcessAsync(
        string inputPath, string outputFolder, ProcessingOptions options, NamePolicy namePolicy,
        ISet<string> claimedNames, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(claimedNames);

        var tempDir = Path.Combine(outputFolder, TempPrefix + Guid.NewGuid().ToString("N"));
        var moved = new List<string>();
        try
        {
            var fileName = Path.GetFileName(inputPath);
            var baseName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = Path.GetExtension(inputPath);
            var wantImage = options.Output is OutputMode.Same or OutputMode.Both;
            var wantPdf = options.Output is OutputMode.Pdf or OutputMode.Both;

            // Names are decided before any work so a clash costs nothing.
            var targetImage = wantImage ? baseName + extension : null;
            var targetPdf = wantPdf ? baseName + ".pdf" : null;
            if (FirstClaimed(claimedNames, targetImage, targetPdf) is { } clash)
                return Failed(inputPath, $"output name '{clash}' is already used by another file in this batch");
            (targetImage, targetPdf) = ResolveNames(outputFolder, baseName, extension, targetImage, targetPdf, namePolicy);
            if (FirstClaimed(claimedNames, targetImage, targetPdf) is { } clash2)
                return Failed(inputPath, $"output name '{clash2}' is already used by another file in this batch");

            Directory.CreateDirectory(tempDir);
            var tempImage = Path.Combine(tempDir, fileName);
            var result = await ImagePipeline.RunAsync(inputPath, tempImage, options, ct).ConfigureAwait(false);
            if (result.Status == Status.Error)
                return new FileResult(inputPath, Status.Error, result.Messages, []);

            var messages = new List<string>(result.Messages);
            var tempPdf = Path.Combine(tempDir, baseName + ".pdf");
            if (wantPdf)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(() => PdfImageWriter.Write(tempImage, result.FinalResolution, tempPdf), ct).ConfigureAwait(false);
                messages.Add("PDF 1.6 written");
            }

            ct.ThrowIfCancellationRequested();
            if (targetImage is not null) moved.Add(MoveIntoPlace(tempImage, Path.Combine(outputFolder, targetImage)));
            if (targetPdf is not null) moved.Add(MoveIntoPlace(tempPdf, Path.Combine(outputFolder, targetPdf)));

            foreach (var name in new[] { targetImage, targetPdf }.OfType<string>()) claimedNames.Add(name);
            return new FileResult(inputPath, result.Status, messages, moved);
        }
        catch (OperationCanceledException)
        {
            DeleteAll(moved);
            throw;
        }
        catch (Exception ex)
        {
            DeleteAll(moved);
            return Failed(inputPath, Describe(ex));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static FileResult Failed(string input, string message) => new(input, Status.Error, [message], []);

    private static string? FirstClaimed(ISet<string> claimed, params string?[] names) =>
        names.FirstOrDefault(n => n is not null && claimed.Contains(n));

    /// <summary>With <see cref="NamePolicy.Suffix"/> picks the smallest number n for which none of the targets exists, so image and PDF stay paired.</summary>
    private static (string? Image, string? Pdf) ResolveNames(
        string folder, string baseName, string extension, string? image, string? pdf, NamePolicy policy)
    {
        if (policy == NamePolicy.Overwrite) return (image, pdf);

        for (var n = 1; ; n++)
        {
            var stem = n == 1 ? baseName : $"{baseName} ({n})";
            var i = image is null ? null : stem + extension;
            var p = pdf is null ? null : stem + ".pdf";
            if ((i is null || !File.Exists(Path.Combine(folder, i))) && (p is null || !File.Exists(Path.Combine(folder, p))))
                return (i, p);
        }
    }

    private static string MoveIntoPlace(string from, string to)
    {
        File.Move(from, to, overwrite: true);
        return to;
    }

    private static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A human-readable one-line reason: no stack trace, and the cases AE operators will meet in words.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        OutOfMemoryException or MagickResourceLimitErrorException => "not enough memory to process this image",
        ImageTooLargeException or UnsupportedImageException => FirstLine(ex.Message),
        MagickException => $"the image could not be read or written ({FirstLine(ex.Message)})",
        _ => FirstLine(ex.Message),
    };

    private static string FirstLine(string message)
    {
        var line = message.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "unknown error" : line;
    }
}
