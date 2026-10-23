using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Options;

namespace Illux.ImageTool.Tests;

public sealed class BatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-batch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Out => Path.Combine(_dir, "out");

    private string Jpeg(string name, double? ppi = 200) =>
        TestImages.Write(Path.Combine(_dir, "in"), name, 60, 40, ColorSpace.sRGB, 8, ppi);

    private string Corrupt(string name)
    {
        var path = Path.Combine(_dir, "in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        return path;
    }

    private static Task<Status> Run(IReadOnlyList<string> inputs, string outputFolder, TextWriter log, ProcessingOptions? options = null) =>
        BatchRunner.RunAsync(inputs, outputFolder, options ?? ProcessingOptions.Default, log, CancellationToken.None);

    private string[] OutNames() =>
        Directory.Exists(Out) ? Directory.GetFileSystemEntries(Out).Select(p => Path.GetFileName(p)).Order().ToArray() : [];

    [Fact]
    public async Task Batch_ThreeFiles_OneCorrupt_OthersWritten_StatusError()
    {
        var inputs = new[] { Jpeg("a.jpg"), Corrupt("b.jpg"), Jpeg("c.jpg") };
        var log = new StringWriter();

        var status = await Run(inputs, Out, log);

        Assert.Equal(Status.Error, status);
        Assert.Equal(["a.jpg", "c.jpg"], OutNames());
        Assert.Contains("ERROR: b.jpg: ", log.ToString());
        Assert.Contains("Result: Error (3 files)", log.ToString());
    }

    [Fact]
    public async Task Batch_UnchangedFileStillWritten()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var log = new StringWriter();

        var status = await Run([input], Out, log);

        Assert.Equal(Status.Ok, status);
        Assert.Contains("Result: OK (1 files)", log.ToString());
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(Path.Combine(Out, "a.jpg")));
    }

    [Fact]
    public async Task Batch_OutputPdf_WritesOnlyPdf()
    {
        var input = Jpeg("a.jpg");

        var status = await Run([input], Out, new StringWriter(), ProcessingOptions.Default with { Output = OutputMode.Pdf });

        Assert.Equal(Status.Ok, status);
        Assert.Equal(["a.pdf"], OutNames());
        Assert.StartsWith("%PDF-1.6", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(Out, "a.pdf")), 0, 8));
    }

    [Fact]
    public async Task Batch_OutputBoth_WritesImageAndPdf()
    {
        var input = Jpeg("a.jpg");

        var status = await Run([input], Out, new StringWriter(), ProcessingOptions.Default with { Output = OutputMode.Both });

        Assert.Equal(Status.Ok, status);
        Assert.Equal(["a.jpg", "a.pdf"], OutNames());
    }

    [Fact]
    public async Task Batch_SameBaseName_SecondIsError()
    {
        var inputs = new[]
        {
            Jpeg("a.jpg"),
            TestImages.Write(Path.Combine(_dir, "in"), "a.tif", 60, 40, ColorSpace.sRGB, 8, 200),
        };
        var log = new StringWriter();

        var status = await Run(inputs, Out, log, ProcessingOptions.Default with { Output = OutputMode.Pdf });

        Assert.Equal(Status.Error, status);
        Assert.Equal(["a.pdf"], OutNames());
        Assert.Contains("ERROR: a.tif: ", log.ToString());
        Assert.DoesNotContain("ERROR: a.jpg", log.ToString());
    }

    [Fact]
    public async Task Batch_ExistingOutput_Overwritten()
    {
        var input = Jpeg("a.jpg");
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "a.jpg"), "old");

        var status = await Run([input], Out, new StringWriter());

        Assert.Equal(Status.Ok, status);
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(Path.Combine(Out, "a.jpg")));
        Assert.Equal(["a.jpg"], OutNames());
    }

    [Fact]
    public async Task Suffix_Policy_AddsNumber()
    {
        var input = Jpeg("a.jpg");
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "a.jpg"), "old");
        File.WriteAllText(Path.Combine(Out, "a (2).jpg"), "old2");

        var result = await FileProcessor.ProcessAsync(input, Out, ProcessingOptions.Default, NamePolicy.Suffix,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal([Path.Combine(Out, "a (3).jpg")], result.Outputs);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Out, "a.jpg")));
        Assert.Equal("old2", File.ReadAllText(Path.Combine(Out, "a (2).jpg")));
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(Path.Combine(Out, "a (3).jpg")));
    }

    [Fact]
    public async Task Batch_OutputFolderMissing_IsErrorNoCrash()
    {
        var input = Jpeg("a.jpg");
        // A folder below an existing file can never be created.
        var impossible = Path.Combine(input, "sub");
        var log = new StringWriter();

        var status = await Run([input, Jpeg("b.jpg")], impossible, log);

        Assert.Equal(Status.Error, status);
        var text = log.ToString();
        Assert.Contains("ERROR: a.jpg: ", text);
        Assert.Contains("ERROR: b.jpg: ", text);
        Assert.Contains("Result: Error (2 files)", text);
    }

    [Fact]
    public async Task Batch_MissingOutputFolder_IsCreated()
    {
        var nested = Path.Combine(_dir, "x", "y");

        var status = await Run([Jpeg("a.jpg")], nested, new StringWriter());

        Assert.Equal(Status.Ok, status);
        Assert.True(File.Exists(Path.Combine(nested, "a.jpg")));
    }

    [Fact]
    public async Task Batch_LockedInput_IsError()
    {
        var locked = Jpeg("a.jpg");
        var good = Jpeg("b.jpg");
        var log = new StringWriter();

        Status status;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            status = await Run([locked, good], Out, log);

        Assert.Equal(Status.Error, status);
        Assert.Equal(["b.jpg"], OutNames());
        Assert.Contains("ERROR: a.jpg: ", log.ToString());
    }

    [Fact]
    public async Task Batch_NoTempFolderLeftBehind()
    {
        var inputs = new[] { Jpeg("a.jpg"), Corrupt("b.jpg") };

        await Run(inputs, Out, new StringWriter(), ProcessingOptions.Default with { Output = OutputMode.Both });

        Assert.DoesNotContain(OutNames(), n => n.StartsWith(".illux-tmp-"));
        Assert.Empty(Directory.GetDirectories(Out));
    }

    [Fact]
    public async Task Batch_LogLinesFormat()
    {
        var inputs = new[] { Jpeg("a.jpg", ppi: 300), Jpeg("b.jpg", ppi: null), Corrupt("c.jpg") };
        var log = new StringWriter();

        var status = await Run(inputs, Out, log);

        Assert.Equal(Status.Error, status);
        var lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("a.jpg: no change needed", lines[0]);
        Assert.StartsWith("WARNING: b.jpg: no resolution stored — assumed 72 ppi; ", lines[1]);
        Assert.StartsWith("ERROR: c.jpg: ", lines[2]);
        Assert.Equal("Result: Error (3 files)", lines[3]);
    }

    [Fact]
    public async Task Batch_NoInputs_IsWarning()
    {
        var log = new StringWriter();

        var status = await Run([], Out, log);

        Assert.Equal(Status.Warning, status);
        Assert.Contains("No input files", log.ToString());
        Assert.Contains("Result: Warning (0 files)", log.ToString());
    }
}
