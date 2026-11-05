using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Illux.ImageTool.Pdf.Input;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using Xunit.Abstractions;

namespace Illux.ImageTool.Tests;

/// <summary>Runs only when <c>ILLUX_PDF_MEMORY_CHECK</c> is <c>1</c> (a CI step of its own on both OSes).</summary>
public sealed class FactWhenMemoryCheckAttribute : FactAttribute
{
    public FactWhenMemoryCheckAttribute()
    {
        if (Environment.GetEnvironmentVariable("ILLUX_PDF_MEMORY_CHECK") != "1")
            Skip = "CI only: set ILLUX_PDF_MEMORY_CHECK=1 (takes minutes and several GB of disk).";
        else if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Skip = "Measures peak memory on Windows and macOS only.";
    }
}

/// <summary>
/// The memory a PDF takes does not grow with its number of images (final review I2): the command-line tool processes a
/// one-page PDF and a six-page PDF of the same large image, each in a process of its own, and the six-page run's peak
/// memory may be at most 1.5 × the one-page run's. Each page holds its own 5906 × 3937 px RGB Flate image (noise, so the
/// re-encoded data does not compress away) drawn 150 × 100 cm: about 100 ppi, rule Large → 200 ppi, 11811 × 7874 px.
/// </summary>
public sealed class PdfMemoryCheckTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 5906, Height = 3937;
    private const double WidthCm = 150, HeightCm = 100;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdfmem-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [FactWhenMemoryCheck]
    public void Pdf_SixLargeImages_PeakMemoryDoesNotGrowWithThePages()
    {
        var data = NoiseImage();
        (string Entries, byte[] Data) Image(Illux.ImageTool.Pdf.PdfObjectWriter _) =>
            ($"/Type /XObject /Subtype /Image /Width {Width} /Height {Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", data);
        var one = TestPdfs.CustomPages(Path.Combine(_dir, "in"), "one.pdf", (Image, WidthCm, HeightCm));
        var six = TestPdfs.CustomPages(Path.Combine(_dir, "in"), "six.pdf", Enumerable.Repeat<(Func<Illux.ImageTool.Pdf.PdfObjectWriter, (string, byte[])>, double, double)>((Image, WidthCm, HeightCm), 6).ToArray());

        var first = RunCli(one);
        var all = RunCli(six);

        var ratio = (double)all.PeakBytes / first.PeakBytes;
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"PDF memory check ({(OperatingSystem.IsWindows() ? "Windows" : "macOS")}): 1 page {Mb(first.PeakBytes)} MB peak, {first.Time.TotalSeconds:0.0} s; " +
            $"6 pages {Mb(all.PeakBytes)} MB peak, {all.Time.TotalSeconds:0.0} s; ratio {ratio:0.00} (limit 1.50); " +
            $"input {Mb(new FileInfo(six).Length)} MB, output {Mb(new FileInfo(all.Output).Length)} MB");
        output.WriteLine(summary);
        output.WriteLine(first.Log);
        output.WriteLine(all.Log);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } stepSummary)
            File.AppendAllText(stepSummary, summary + Environment.NewLine);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, all.ExitCode);
        var images = PdfImageScanner.Scan(all.Output).Images;
        Assert.Equal(6, images.Count);
        Assert.Equal(Enumerable.Repeat((11811, 7874), 6), Sizes(all.Output, images.Select(i => i.Image)));
        Assert.True(ratio <= 1.5, summary);
    }

    private sealed record Run(int ExitCode, long PeakBytes, TimeSpan Time, string Output, string Log);

    /// <summary>
    /// Runs the command-line tool (<c>ae "&lt;pdf&gt;" "&lt;out&gt;"</c>, default rules) in a process of its own. Peak
    /// memory: the process's peak working set, read while it runs (Windows), or the maximum resident set size that
    /// <c>/usr/bin/time -l</c> reports (macOS).
    /// </summary>
    private Run RunCli(string pdf)
    {
        var outDir = Directory.CreateDirectory(Path.Combine(_dir, "out-" + Path.GetFileNameWithoutExtension(pdf))).FullName;
        var cli = Path.Combine(AppContext.BaseDirectory, "IlluxImageTool.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.ChangeExtension(cli, ".exe"))
            : new ProcessStartInfo("/usr/bin/time") { ArgumentList = { "-l", dotnet, cli } };
        foreach (var argument in new[] { "ae", pdf, outDir }) start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        long peak = 0;
        while (!process.HasExited)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    process.Refresh();
                    peak = Math.Max(peak, process.PeakWorkingSet64);
                }
                catch (InvalidOperationException) { } // it has just exited
            }
            Thread.Sleep(20);
        }
        process.WaitForExit();
        clock.Stop();
        var log = $"{Path.GetFileName(pdf)}: exit {process.ExitCode}\n{stdout.Result}{stderr.Result}";
        if (!OperatingSystem.IsWindows())
        {
            var match = Regex.Match(stderr.Result, @"(\d+)\s+maximum resident set size");
            Assert.True(match.Success, "no maximum resident set size from /usr/bin/time -l:\n" + log);
            peak = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); // bytes on macOS
        }
        Assert.True(peak > 0, "no peak memory measured:\n" + log);
        return new Run(process.ExitCode, peak, clock.Elapsed, Path.Combine(outDir, Path.GetFileName(pdf)), log);
    }

    /// <summary>Deflated RGB noise (a fixed seed), so the image data keeps its size through resampling and Flate.</summary>
    private static byte[] NoiseImage()
    {
        var samples = new byte[(long)Width * Height * 3];
        new Random(20261105).NextBytes(samples);
        using var result = new MemoryStream();
        using (var zlib = new ZLibStream(result, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(samples);
        return result.ToArray();
    }

    /// <summary>The images' pixel sizes, read through a file stream (PdfDocument.Open(path) would read the whole file).</summary>
    private static List<(int Width, int Height)> Sizes(string pdf, IEnumerable<PdfObjectId> ids)
    {
        using var file = File.OpenRead(pdf);
        using var document = PdfDocument.Open(file);
        return ids.Select(id =>
        {
            var dictionary = Assert.IsType<StreamToken>(document.Structure.GetObject(new IndirectReference(id.Number, id.Generation)).Data).StreamDictionary;
            return (((NumericToken)dictionary.Data["Width"]).Int, ((NumericToken)dictionary.Data["Height"]).Int);
        }).ToList();
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);
}
