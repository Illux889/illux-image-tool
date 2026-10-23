using ImageMagick;
using Illux.ImageTool.Watch;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Tests;

public sealed class WatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-watch-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string In => Path.Combine(_dir, "In");
    private string Out => Path.Combine(_dir, "Out");
    private string Error => Path.Combine(_dir, "Error");
    private string Originals => Path.Combine(_dir, "Originals");

    private WatchedFolder Folder(params string[] parameters) => new()
    {
        Name = "test", In = In, Out = Out, Error = Error, Parameters = [.. parameters],
    };

    private FolderScanner Scanner(WatchedFolder? folder = null) => new(folder ?? Folder(), _log);

    private string Jpeg(string name, double? ppi = 200) => TestImages.Write(In, name, 60, 40, ColorSpace.sRGB, 8, ppi);

    private string Corrupt(string name)
    {
        Directory.CreateDirectory(In);
        var path = Path.Combine(In, name);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        return path;
    }

    private static async Task ScanTwice(FolderScanner scanner)
    {
        await scanner.ScanOnceAsync(CancellationToken.None);
        await scanner.ScanOnceAsync(CancellationToken.None);
    }

    private static string[] Names(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)).Order().ToArray()
            : [];

    [Fact]
    public async Task Scan_ProcessesStableFile_ToOut_OriginalToOriginals()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var bytes = File.ReadAllBytes(input);
        var scanner = Scanner();

        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(input)); // first sight only records size and time

        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(input));
        Assert.Equal(["a.jpg"], Names(Out));
        Assert.Equal(["a.jpg"], Names(Originals));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Originals, "a.jpg")));
        Assert.False(scanner.Busy);
        Assert.Empty(Names(In));
    }

    [Fact]
    public async Task Scan_FileStillGrowing_NotProcessed()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var scanner = Scanner();
        await scanner.ScanOnceAsync(CancellationToken.None);

        await using (var append = new FileStream(input, FileMode.Append, FileAccess.Write))
            append.Write([0, 0, 0, 0]);
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(input));
        Assert.Empty(Names(Out));

        await scanner.ScanOnceAsync(CancellationToken.None); // unchanged since the previous scan now
        Assert.False(File.Exists(input));
        Assert.Equal(["a.jpg"], Names(Originals));
    }

    [Fact]
    public async Task Scan_FileLockedByWriter_NotProcessed()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var scanner = Scanner();

        await using (new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await ScanTwice(scanner);
            Assert.True(File.Exists(input));
            Assert.Empty(Names(Out));
        }

        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.False(File.Exists(input));
        Assert.Equal(["a.jpg"], Names(Out));
    }

    [Fact]
    public async Task Scan_CorruptFile_ToErrorWithTxt()
    {
        var input = Corrupt("bad.jpg");
        var bytes = File.ReadAllBytes(input);

        await ScanTwice(Scanner());

        Assert.False(File.Exists(input));
        Assert.Equal(["bad.jpg", "bad.jpg.error.txt"], Names(Error));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Error, "bad.jpg")));
        var text = File.ReadAllText(Path.Combine(Error, "bad.jpg.error.txt"));
        Assert.Contains("bad.jpg", text);
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", text);
        Assert.True(text.Trim().Split('\n').Length >= 3, "timestamp, file name and at least one message expected");
        Assert.Empty(Names(Out));
        Assert.Empty(Names(Originals));
    }

    [Fact]
    public async Task Scan_OutNameClash_GetsSuffix()
    {
        Directory.CreateDirectory(Out);
        File.WriteAllBytes(Path.Combine(Out, "a.jpg"), [9, 9, 9]);
        Jpeg("a.jpg", ppi: 300);

        await ScanTwice(Scanner());

        Assert.Equal(["a (2).jpg", "a.jpg"], Names(Out));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(Out, "a.jpg")));
    }

    [Fact]
    public async Task Scan_OriginalsNameClash_GetsSuffix()
    {
        var scanner = Scanner();
        var first = File.ReadAllBytes(Jpeg("a.jpg", ppi: 300));
        await ScanTwice(scanner);
        var second = File.ReadAllBytes(TestImages.Write(In, "a.jpg", 50, 30, ColorSpace.sRGB, 8, 300));
        await ScanTwice(scanner);

        Assert.Equal(["a (2).jpg", "a.jpg"], Names(Originals));
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(Originals, "a.jpg")));
        Assert.Equal(second, File.ReadAllBytes(Path.Combine(Originals, "a (2).jpg")));
    }

    [Fact]
    public async Task Scan_IgnoresTempAndHiddenFiles()
    {
        Directory.CreateDirectory(In);
        foreach (var name in new[] { "~$a.jpg", ".hidden.jpg", "b.tmp", "c.part", "D.CRDOWNLOAD" })
            File.WriteAllBytes(Path.Combine(In, name), [1, 2, 3]);
        var hidden = Jpeg("e.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = Jpeg("f.jpg");
        File.SetAttributes(system, FileAttributes.System);
        TestImages.Write(Path.Combine(In, ".illux-tmp-abc"), "g.jpg", 60, 40, ColorSpace.sRGB, 8, 200);
        var before = Names(In);

        await ScanTwice(Scanner());

        Assert.Equal(before, Names(In));
        Assert.Empty(Names(Out));
        Assert.Empty(Names(Error));
        Assert.Empty(Names(Originals));
    }

    [Fact]
    public async Task Scan_BadParameters_NoFilesMoved()
    {
        Jpeg("a.jpg");
        var scanner = Scanner(Folder("bogus"));

        await ScanTwice(scanner);

        Assert.Equal(["a.jpg"], Names(In));
        Assert.Empty(Names(Out));
        Assert.Empty(Names(Error));
        Assert.Empty(Names(Originals));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("bogus"));
    }

    [Fact]
    public async Task Scan_MissingInFolder_LoggedOnce()
    {
        var scanner = Scanner();

        await ScanTwice(scanner);

        Assert.Single(_log.Entries, e => e.Message.Contains("does not exist"));
        Assert.False(Directory.Exists(In));
    }

    [Theory]
    [InlineData("Originals")]
    [InlineData("Out")]
    [InlineData("Error")]
    public async Task Scan_FolderSharedWithIn_IsMisconfigured_NoFilesMoved(string which)
    {
        Jpeg("a.jpg");
        var folder = Folder();
        if (which == "Out") folder.Out = In;
        if (which == "Error") folder.Error = In;
        if (which == "Originals") folder.Originals = In;

        await ScanTwice(Scanner(folder));

        Assert.Equal(["a.jpg"], Names(In));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Scan_Warning_TreatedAsSuccess_AndAppendedToWarningsLog()
    {
        Jpeg("a.jpg", ppi: null); // no ppi: assumed 72, which is a Warning

        await ScanTwice(Scanner());

        Assert.Equal(["_warnings.log", "a.jpg"], Names(Out));
        Assert.Equal(["a.jpg"], Names(Originals));
        var log = File.ReadAllText(Path.Combine(Out, "_warnings.log"));
        Assert.Contains("a.jpg", log);
        Assert.Contains("72", log);
    }

    [Fact]
    public async Task Scan_OutputPdf_IsVerifiedAndOriginalMoved()
    {
        Jpeg("a.jpg", ppi: 300);

        await ScanTwice(Scanner(Folder("output=pdf")));

        Assert.Equal(["a.pdf"], Names(Out));
        Assert.Equal(["a.jpg"], Names(Originals));
    }

    [Fact]
    public async Task Scan_NeverDeletesOriginal()
    {
        var inputs = new[] { Jpeg("good.jpg", 300), Corrupt("bad.jpg"), Jpeg("nodpi.jpg", null) };
        var originalBytes = inputs.Select(File.ReadAllBytes).ToList();

        await ScanTwice(Scanner());

        var kept = new[] { Originals, Error }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d))
            .Select(File.ReadAllBytes)
            .ToList();
        foreach (var bytes in originalBytes)
            Assert.Contains(kept, k => k.SequenceEqual(bytes));
        Assert.Empty(Names(In));
    }

    [Fact]
    public void OriginalsOrDefault_IsSiblingOfIn_EvenWithTrailingSeparator()
    {
        var folder = new WatchedFolder { In = Path.Combine(_dir, "customer", "In") + Path.DirectorySeparatorChar };

        Assert.Equal(Path.Combine(_dir, "customer", "Originals"), folder.OriginalsOrDefault);

        folder.Originals = Path.Combine(_dir, "elsewhere");
        Assert.Equal(Path.Combine(_dir, "elsewhere"), folder.OriginalsOrDefault);
    }

    [Fact]
    public void ConfigStore_RoundTrip()
    {
        var path = Path.Combine(_dir, "cfg", "config.json");
        var config = new WatcherConfig
        {
            CheckForUpdates = false,
            PollSeconds = 9,
            Folders =
            [
                new WatchedFolder
                {
                    Name = "Customer A", In = @"C:\A\In", Out = @"C:\A\Out", Error = @"C:\A\Error",
                    Originals = @"C:\A\Orig", Parameters = ["8bit", "upsample=300"], Enabled = false,
                },
                new WatchedFolder { Name = "B", In = @"\\srv\b\In", Out = @"\\srv\b\Out", Error = @"\\srv\b\Error" },
            ],
        };

        ConfigStore.Save(config, path);
        var loaded = ConfigStore.Load(path);

        Assert.False(loaded.CheckForUpdates);
        Assert.Equal(9, loaded.PollSeconds);
        Assert.Equal(2, loaded.Folders.Count);
        var a = loaded.Folders[0];
        Assert.Equal(("Customer A", @"C:\A\In", @"C:\A\Out", @"C:\A\Error", @"C:\A\Orig", false),
            (a.Name, a.In, a.Out, a.Error, a.Originals, a.Enabled));
        Assert.Equal(["8bit", "upsample=300"], a.Parameters);
        var b = loaded.Folders[1];
        Assert.Null(b.Originals);
        Assert.True(b.Enabled);
        Assert.Empty(b.Parameters);
        Assert.Equal(@"\\srv\b\Originals", b.OriginalsOrDefault);
        Assert.Contains("\n", File.ReadAllText(path)); // indented
        Assert.Equal(["config.json"], Names(Path.GetDirectoryName(path)!)); // no temp file left behind
    }

    [Fact]
    public void ConfigStore_Missing_ReturnsDefault()
    {
        var config = ConfigStore.Load(Path.Combine(_dir, "nope.json"));

        Assert.Empty(config.Folders);
        Assert.True(config.CheckForUpdates);
        Assert.Equal(5, config.PollSeconds);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    public void ConfigStore_InvalidJson_ThrowsConfigException(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, content);

        Assert.Throws<ConfigException>(() => ConfigStore.Load(path));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

[Collection("AppRoot")]
public sealed class ConfigStoreDefaultPathTests : AppRootTestBase
{
    [Fact]
    public void ConfigStore_DefaultPath_IsAppPathsConfigFile()
    {
        var config = new WatcherConfig { PollSeconds = 7 };

        ConfigStore.Save(config);

        Assert.True(File.Exists(Path.Combine(AppRoot, "config.json")));
        Assert.Equal(7, ConfigStore.Load().PollSeconds);
    }
}
