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
            foreach (var file in Directory.EnumerateFiles(outDir, "*.pdf", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(target, $"{prefix}-{Path.GetFileName(testDir)}-{Path.GetFileName(file)}"), overwrite: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
