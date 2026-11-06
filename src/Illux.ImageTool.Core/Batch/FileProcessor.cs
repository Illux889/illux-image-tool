using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Options;
using Illux.ImageTool.Pdf;
using Illux.ImageTool.Pdf.Input;
using Illux.ImageTool.Runtime;

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
/// produced in a temporary folder and moved into place only when complete, so a failing file leaves nothing behind.
/// A PDF (by content, <see cref="SourceImage.IsPdf"/>) goes through <see cref="PdfPipeline"/> and gives exactly one
/// PDF under its own name, whatever the output mode.
/// The temporary folder is next to the destination (R46), except for the macOS folders job (V15, final review C2): it
/// stages in its private work folder, so nothing in Out can be swapped under it, and moves the outputs into Out with
/// an exclusive rename.
/// </summary>
public static class FileProcessor
{
    /// <summary>Name prefix of the temporary folder made in the destination for each file.</summary>
    internal const string TempPrefix = ".illux-tmp-";

    /// <param name="claimedNames">
    /// File names already written in this batch (compared as given; use a case-insensitive set). A target name in it is
    /// an Error; names written by this call are added on success.
    /// </param>
    /// <param name="stagingFolder">
    /// macOS only, with <see cref="NamePolicy.Suffix"/>: a private folder of this account (a real folder that group and
    /// others cannot write to, checked) in which each file gets its own 0700 folder; null for the temporary folder in
    /// <paramref name="outputFolder"/>.
    /// </param>
    public static async Task<FileResult> ProcessAsync(
        string inputPath, string outputFolder, ProcessingOptions options, NamePolicy namePolicy,
        ISet<string> claimedNames, CancellationToken ct, string? stagingFolder = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputFolder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(claimedNames);
        if (stagingFolder is not null && (OperatingSystem.IsWindows() || namePolicy != NamePolicy.Suffix))
            throw new ArgumentException("A staging folder is only used on macOS, with NamePolicy.Suffix.", nameof(stagingFolder));

        // I2: an unsafe ImageMagick temp folder (another user's, or a link) refuses the file rather than being used.
        if (MagickSetup.TempDirectoryProblem() is { } tempProblem) return Failed(inputPath, tempProblem);

        var staged = stagingFolder is not null;
        var tempDir = staged
            ? Path.Combine(stagingFolder!, Guid.NewGuid().ToString("N"))
            : Path.Combine(outputFolder, TempPrefix + Guid.NewGuid().ToString("N"));
        var tempMade = false;
        var moved = new List<string>();
        try
        {
            var fileName = Path.GetFileName(inputPath);
            var baseName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = Path.GetExtension(inputPath);
            // A PDF in gives exactly one PDF out, under its own name, whatever output= says (it takes the image's place).
            var isPdf = SourceImage.IsPdf(inputPath);
            var wantImage = isPdf || (options.Output is OutputMode.Same or OutputMode.Both);
            var wantPdf = !isPdf && (options.Output is OutputMode.Pdf or OutputMode.Both);

            // Names are decided before any work so a clash costs nothing.
            var targetImage = wantImage ? baseName + extension : null;
            var targetPdf = wantPdf ? baseName + ".pdf" : null;
            if (FirstClaimed(claimedNames, targetImage, targetPdf) is { } clash)
                return Failed(inputPath, $"output name '{clash}' is already used by another file in this batch");
            (targetImage, targetPdf) = ResolveNames(outputFolder, baseName, extension, targetImage, targetPdf, namePolicy, staged);
            if (FirstClaimed(claimedNames, targetImage, targetPdf) is { } clash2)
                return Failed(inputPath, $"output name '{clash2}' is already used by another file in this batch");
            if (new[] { targetImage, targetPdf }.Any(t => t is not null && FileIdentity.AreSame(Path.Combine(outputFolder, t), inputPath)))
                return Failed(inputPath, "output would replace the input file; choose a different output folder");

            if (staged)
            {
                if (PrepareStaging(stagingFolder!, tempDir, ref tempMade) is { } stagingProblem) return Failed(inputPath, stagingProblem);
                Directory.CreateDirectory(outputFolder); // Out (made by the folders job: owned by the folder account)
            }
            else
            {
                TempFolder.Create(tempDir); // R46: only this account (and on Windows SYSTEM and Administrators) may change it
                tempMade = true;
                LinkGuard.ThrowIfLinkedFolder(tempDir); // R44: nothing is written through a folder swapped for a link
            }
            var tempImage = Path.Combine(tempDir, fileName);
            var result = isPdf
                ? await PdfPipeline.RunAsync(inputPath, tempImage, options, ct).ConfigureAwait(false)
                : await ImagePipeline.RunAsync(inputPath, tempImage, options, ct).ConfigureAwait(false);
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
            LinkGuard.ThrowIfLinkedFolder(tempDir);
            var overwrite = namePolicy == NamePolicy.Overwrite;
            if (targetImage is not null) moved.Add(MoveIntoPlace(tempImage, Path.Combine(outputFolder, targetImage), overwrite, staged));
            if (targetPdf is not null) moved.Add(MoveIntoPlace(tempPdf, Path.Combine(outputFolder, targetPdf), overwrite, staged));

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
            // Staged: the recursive delete runs only in the private staging folder (C2).
            if (tempMade)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// C2: the staging folder must be this account's own (real folder, group and others cannot write); the file's own
    /// folder is created in it with mode 0700 and checked the same way. Null when ready, otherwise why not.
    /// </summary>
    private static string? PrepareStaging(string stagingFolder, string tempDir, ref bool tempMade)
    {
        if (!UnixNative.IsSupported) return "the private work folder can only be used on macOS on Apple silicon";
        var me = UnixNative.EffectiveUserId;
        if (TrustedFolder.Problem(stagingFolder, me) is { } reason)
            return $"the private work folder '{stagingFolder}' {reason}; reinstall Illux Image Tool to repair it";
        Directory.CreateDirectory(tempDir, TempFolder.OwnerOnly);
        tempMade = true;
        return TrustedFolder.Problem(tempDir, me) is { } tempReason ? $"the work folder '{tempDir}' {tempReason}" : null;
    }

    /// <summary>
    /// macOS folders job: removes per-file folders (32 hex digits) older than <paramref name="maxAge"/> from its private
    /// staging folder, left by a job that was stopped in the middle of a file. Nothing outside that folder is touched.
    /// </summary>
    internal static int RemoveStaleStaging(string stagingFolder, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!UnixNative.IsSupported || TrustedFolder.Problem(stagingFolder, UnixNative.EffectiveUserId) is not null) return 0;
        var removed = 0;
        foreach (var folder in new DirectoryInfo(stagingFolder).EnumerateDirectories())
        {
            if (folder.Name.Length != 32 || !folder.Name.All(char.IsAsciiHexDigitLower) || LinkGuard.IsLink(folder)) continue;
            if (nowUtc - folder.LastWriteTimeUtc < maxAge) continue;
            try
            {
                folder.Delete(recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next start.
            }
        }
        return removed;
    }

    private static FileResult Failed(string input, string message) => new(input, Status.Error, [message], []);

    private static string? FirstClaimed(ISet<string> claimed, params string?[] names) =>
        names.FirstOrDefault(n => n is not null && claimed.Contains(n));

    /// <summary>
    /// With <see cref="NamePolicy.Suffix"/> picks the smallest number n for which none of the targets exists, so image
    /// and PDF stay paired. Staged (macOS), a link of that name (even one pointing nowhere) counts as taken too.
    /// </summary>
    private static (string? Image, string? Pdf) ResolveNames(
        string folder, string baseName, string extension, string? image, string? pdf, NamePolicy policy, bool staged)
    {
        if (policy == NamePolicy.Overwrite) return (image, pdf);

        bool Taken(string name)
        {
            var path = Path.Combine(folder, name);
            return File.Exists(path) || (staged && LinkGuard.IsLink(path));
        }

        for (var n = 1; ; n++)
        {
            var stem = n == 1 ? baseName : $"{baseName} ({n})";
            var i = image is null ? null : stem + extension;
            var p = pdf is null ? null : stem + ".pdf";
            if ((i is null || !Taken(i)) && (p is null || !Taken(p)))
                return (i, p);
        }
    }

    /// <summary>
    /// With <see cref="NamePolicy.Suffix"/> a file that appeared since the name was chosen is never replaced: the move
    /// fails and the file is retried later. Staged (macOS), the move is one exclusive rename (renamex_np RENAME_EXCL),
    /// so it never replaces anything nor goes through a link, also when the name was taken after the check.
    /// </summary>
    private static string MoveIntoPlace(string from, string to, bool overwrite, bool staged)
    {
        if (staged && UnixNative.IsSupported)
        {
            UnixNative.MoveExclusive(from, to);
            return to;
        }
        LinkGuard.ThrowIfLinkedFile(to);
        File.Move(from, to, overwrite);
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
