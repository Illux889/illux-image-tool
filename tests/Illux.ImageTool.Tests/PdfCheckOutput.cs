namespace Illux.ImageTool.Tests;

/// <summary>
/// CI only: when <c>ILLUX_PDF_CHECK_DIR</c> is set, the PDFs a test wrote are copied into that folder before the test's
/// own folder is deleted, so a later CI step can run <c>qpdf --check</c> on every one of them (an independent reader).
/// Without the variable nothing happens.
/// </summary>
internal static class PdfCheckOutput
{
    public static void Keep(string testDir, string prefix)
    {
        var target = Environment.GetEnvironmentVariable("ILLUX_PDF_CHECK_DIR");
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            var outDir = Path.Combine(testDir, "out");
            if (!Directory.Exists(outDir)) return;
            Directory.CreateDirectory(target);
            // Any case of the extension (Card.PDF): Directory.EnumerateFiles' pattern is case sensitive on macOS.
            var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);
            var number = 0;
            foreach (var file in files)
            {
                // A short counter, not the folder path: it keeps equal names from different subfolders (or a test's
                // very long output path) from colliding or exceeding the file name limit.
                number++;
                File.Copy(file, Path.Combine(target, $"{prefix}-{Path.GetFileName(testDir)}-{number}-{Path.GetFileName(file)}"), overwrite: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
