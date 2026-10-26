using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Watch;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Tests;

public sealed class WatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-watch-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();
    private readonly FakeClock _clock = new();
    private volatile bool _updating;

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

    private FolderScanner Scanner(WatchedFolder? folder = null) => new(folder ?? Folder(), _log, _clock, () => _updating);

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
    public async Task Scan_WhileAnUpdateIsPending_PicksUpNothing_ThenResumes()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var scanner = Scanner();
        _updating = true;

        await ScanTwice(scanner);
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(input));
        Assert.Empty(Names(Out));
        Assert.False(scanner.Busy);
        Assert.Single(_log.Entries, e => e.Message.Contains("update", StringComparison.OrdinalIgnoreCase)); // said once

        _updating = false;
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(["a.jpg"], Names(Out));
        Assert.Equal(["a.jpg"], Names(Originals));
    }

    [Fact]
    public async Task Scan_ClaimsBusyBeforeLookingForTheUpdatingFlag()
    {
        // The updater writes updating.flag and then checks Busy again (like the CLI, R30): a scanner must say it is
        // busy before it looks for the flag, or a file could start right after the updater's second look.
        Jpeg("a.jpg", ppi: 300);
        FolderScanner? scanner = null;
        var busyWhenAsked = new List<bool>();
        scanner = new FolderScanner(Folder(), _log, _clock, () =>
        {
            busyWhenAsked.Add(scanner!.Busy);
            return true;
        });

        await ScanTwice(scanner);

        Assert.Equal([true], busyWhenAsked);
        Assert.False(scanner.Busy);
    }

    private string ToolTempFolder()
    {
        var path = Path.Combine(Out, ".illux-tmp-" + Guid.NewGuid().ToString("N"));
        TempFolder.Create(path); // exactly what FileProcessor does
        return path;
    }

    private void Age(string folder)
    {
        var old = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1) - TimeSpan.FromMinutes(1);
        Directory.SetCreationTimeUtc(folder, old);
        Directory.SetLastWriteTimeUtc(folder, old);
    }

    [Fact]
    public void RemoveStaleTempFolders_OnlyOldTempFoldersMadeByTheToolInOut()
    {
        var old = ToolTempFolder();
        File.WriteAllText(Path.Combine(old, "half.jpg"), "partial");
        var fresh = ToolTempFolder();
        var customer = Directory.CreateDirectory(Path.Combine(Out, "illux-tmp-not-ours"));
        Age(old);
        Directory.SetLastWriteTimeUtc(fresh, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5));
        Directory.SetLastWriteTimeUtc(customer.FullName, _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(3));

        Scanner().RemoveStaleTempFolders();

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh)); // may belong to a file being written right now
        Assert.True(Directory.Exists(customer.FullName));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void RemoveStaleTempFolders_PlantedFolderWithInheritedPermissions_IsLeftAlone()
    {
        // N1: anyone who may create folders in Out can make one with this name; the watcher (SYSTEM) must not delete it.
        Directory.CreateDirectory(Out);
        var planted = Directory.CreateDirectory(Path.Combine(Out, ".illux-tmp-" + Guid.NewGuid().ToString("N")));
        File.WriteAllText(Path.Combine(planted.FullName, "x.jpg"), "planted");
        Age(planted.FullName);

        Scanner().RemoveStaleTempFolders();

        Assert.True(File.Exists(Path.Combine(planted.FullName, "x.jpg")));
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(planted.Name));
    }

    [FactWhenJunctionsWork]
    public void RemoveStaleTempFolders_NeverFollowsALink()
    {
        Directory.CreateDirectory(Out);
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere"));
        File.WriteAllText(Path.Combine(elsewhere.FullName, "keep.txt"), "not ours");
        var link = Path.Combine(Out, ".illux-tmp-" + Guid.NewGuid().ToString("N"));
        Assert.True(FactWhenJunctionsWorkAttribute.TryCreateJunction(link, elsewhere.FullName));
        _clock.Advance(TimeSpan.FromDays(3650));

        Scanner().RemoveStaleTempFolders();

        Assert.True(File.Exists(Path.Combine(elsewhere.FullName, "keep.txt")));
        Assert.True(Directory.Exists(link)); // a link is left alone altogether
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(Path.GetFileName(link)));
    }

    [FactWhenJunctionsWork]
    public void RemoveStaleTempFolders_JunctionInsideOurFolder_RemovesTheJunctionNotItsTarget()
    {
        var temp = ToolTempFolder();
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere"));
        File.WriteAllText(Path.Combine(elsewhere.FullName, "canary.txt"), "must survive");
        Directory.CreateDirectory(Path.Combine(temp, "sub"));
        File.WriteAllText(Path.Combine(temp, "sub", "half.jpg"), "partial");
        Assert.True(FactWhenJunctionsWorkAttribute.TryCreateJunction(Path.Combine(temp, "sub", "link"), elsewhere.FullName));
        Assert.True(FactWhenJunctionsWorkAttribute.TryCreateJunction(Path.Combine(temp, "link"), elsewhere.FullName));
        Age(temp);

        Scanner().RemoveStaleTempFolders();

        Assert.False(Directory.Exists(temp));
        Assert.True(File.Exists(Path.Combine(elsewhere.FullName, "canary.txt")));
        Assert.Equal(["canary.txt"], Names(elsewhere.FullName));
    }

    [Fact]
    public void RemoveStaleTempFolders_MissingOut_DoesNothing() => Scanner().RemoveStaleTempFolders();

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
    public async Task Scan_OriginalsBlocked_FileParked_NotReprocessedEveryPoll_BacksOff()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var bytes = File.ReadAllBytes(input);
        File.WriteAllBytes(Originals, [1]); // a FILE where the Originals folder should be
        var scanner = Scanner();

        await ScanTwice(scanner); // scan 2 processes and fails to put the original away
        Assert.Equal(1, Count(LogLevel.Information, "processing"));
        Assert.Equal(1, Count(LogLevel.Error));
        Assert.Equal(bytes, File.ReadAllBytes(input));
        Assert.Empty(Names(Out));
        Assert.Empty(Names(Error));

        await scanner.ScanOnceAsync(CancellationToken.None); // scans 3 and 4: parked
        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(1, Count(LogLevel.Information, "processing"));
        Assert.Equal(1, Count(LogLevel.Error));

        _clock.Advance(TimeSpan.FromSeconds(61)); // first retry after 1 minute: fails again, only logged at Debug
        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(2, Count(LogLevel.Information, "processing"));
        Assert.Equal(1, Count(LogLevel.Error));
        Assert.Equal(1, Count(LogLevel.Debug));
        Assert.Empty(Names(Out));

        _clock.Advance(TimeSpan.FromSeconds(61)); // the delay has doubled to 2 minutes: too early
        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(2, Count(LogLevel.Information, "processing"));

        File.Delete(Originals); // fixed by the administrator
        _clock.Advance(TimeSpan.FromMinutes(2));
        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(["a.jpg"], Names(Originals));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Originals, "a.jpg")));
        Assert.Equal(["a.jpg"], Names(Out));
        Assert.Empty(Names(In));
    }

    [Fact]
    public async Task Scan_ParkedFile_ThatChanges_IsRetriedAtOnce()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        File.WriteAllBytes(Originals, [1]);
        var scanner = Scanner();
        await ScanTwice(scanner);
        File.Delete(Originals);

        await using (var append = new FileStream(input, FileMode.Append, FileAccess.Write))
            append.Write([0]);
        await scanner.ScanOnceAsync(CancellationToken.None); // new size: stability check restarts
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.Equal(["a.jpg"], Names(Originals));
    }

    [Fact]
    public async Task Scan_OneFailingFile_DoesNotStarveTheOthers()
    {
        Jpeg("a.jpg", ppi: 300);
        Jpeg("b.jpg", ppi: 300);
        var scanner = Scanner();
        scanner.OutputVerifier = outputs =>
            outputs.Any(o => Path.GetFileName(o) == "a.jpg") ? throw new InvalidOperationException("boom") : null;

        await ScanTwice(scanner);

        Assert.Equal(["a.jpg"], Names(In));
        Assert.Equal(["b.jpg"], Names(Originals));
        Assert.Equal(["b.jpg"], Names(Out)); // the output of the failing file was removed again
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("boom"));
    }

    [Fact]
    public async Task Scan_OutputVerificationFails_OutputsRemoved_OriginalToError()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var bytes = File.ReadAllBytes(input);
        var scanner = Scanner();
        scanner.OutputVerifier = _ => "output 'a.jpg' could not be re-opened";

        await ScanTwice(scanner);

        Assert.Empty(Names(Out));
        Assert.Empty(Names(Originals));
        Assert.Empty(Names(In));
        Assert.Equal(["a.jpg", "a.jpg.error.txt"], Names(Error));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Error, "a.jpg")));
        Assert.Contains("could not be re-opened", File.ReadAllText(Path.Combine(Error, "a.jpg.error.txt")));
    }

    [Fact]
    public void VerifyOutputs_AcceptsGoodFiles_RejectsBadOnes()
    {
        Directory.CreateDirectory(_dir);
        var good = TestImages.Write(_dir, "good.jpg", 20, 10, ColorSpace.sRGB, 8, 200);
        var garbage = Path.Combine(_dir, "garbage.jpg");
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8]);
        var empty = Path.Combine(_dir, "empty.jpg");
        File.WriteAllBytes(empty, []);
        var pdf = Path.Combine(_dir, "good.pdf");
        File.WriteAllText(pdf, "%PDF-1.6\n1 0 obj\n<<>>\nendobj\n%%EOF\r\n \n");
        var truncatedPdf = Path.Combine(_dir, "truncated.pdf");
        File.WriteAllText(truncatedPdf, "%PDF-1.6\n1 0 obj\n<<>>\nendobj\n");
        var wrongVersion = Path.Combine(_dir, "old.pdf");
        File.WriteAllText(wrongVersion, "%PDF-1.4\n%%EOF");

        Assert.Null(FolderScanner.VerifyOutputs([good, pdf]));
        Assert.NotNull(FolderScanner.VerifyOutputs([garbage]));
        Assert.NotNull(FolderScanner.VerifyOutputs([empty]));
        Assert.NotNull(FolderScanner.VerifyOutputs([truncatedPdf]));
        Assert.NotNull(FolderScanner.VerifyOutputs([wrongVersion]));
        Assert.NotNull(FolderScanner.VerifyOutputs([good, garbage]));
    }

    [Fact]
    public async Task Scan_FileVanishesBetweenScans_IsForgottenWithoutErrors()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var scanner = Scanner();
        await scanner.ScanOnceAsync(CancellationToken.None);

        File.Delete(input);
        await scanner.ScanOnceAsync(CancellationToken.None);

        // A new file with the same name starts the stability check from scratch.
        Jpeg("a.jpg", ppi: 300);
        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(["a.jpg"], Names(In));
        await scanner.ScanOnceAsync(CancellationToken.None);
        Assert.Equal(["a.jpg"], Names(Originals));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Scan_FileLockedForMoreThanFiveMinutes_LoggedOnce()
    {
        var input = Jpeg("a.jpg", ppi: 300);
        var scanner = Scanner();

        await using var held = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        await ScanTwice(scanner);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("locked"));

        _clock.Advance(TimeSpan.FromMinutes(6));
        await scanner.ScanOnceAsync(CancellationToken.None);
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("locked"));
    }

    [Theory]
    [InlineData("In")]
    [InlineData("Out")]
    [InlineData("Error")]
    [InlineData("Originals")]
    public async Task Scan_RelativePath_IsMisconfigured_NoFilesMoved(string which)
    {
        Jpeg("a.jpg");
        var folder = Folder();
        var relative = Path.Combine("some", "relative");
        if (which == "In") folder.In = relative;
        if (which == "Out") folder.Out = relative;
        if (which == "Error") folder.Error = relative;
        if (which == "Originals") folder.Originals = relative;

        await ScanTwice(Scanner(folder));

        Assert.Equal(["a.jpg"], Names(In));
        Assert.Empty(Names(Out));
        Assert.Empty(Names(Error));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("full path"));
    }

    [Fact]
    public async Task Scan_OutSameAsOriginals_IsMisconfigured_NoFilesMoved()
    {
        Jpeg("a.jpg");
        var folder = Folder();
        folder.Originals = Out + Path.DirectorySeparatorChar;

        await ScanTwice(Scanner(folder));

        Assert.Equal(["a.jpg"], Names(In));
        Assert.Empty(Names(Out));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Originals"));
    }

    [Fact]
    public async Task Scan_DirectoryTakesTheName_FileGetsNextFreeName()
    {
        Directory.CreateDirectory(Path.Combine(Originals, "a.jpg")); // a folder named like the file counts as taken
        Jpeg("a.jpg", ppi: 300);

        await ScanTwice(Scanner());

        Assert.True(File.Exists(Path.Combine(Originals, "a (2).jpg")));
        Assert.Equal(["a.jpg"], Names(Out));
    }

    private int Count(LogLevel level, string? contains = null) =>
        _log.Entries.Count(e => e.Level == level && (contains is null || e.Message.Contains(contains)));

    [Fact]
    public void OriginalsOrDefault_IsSiblingOfIn_EvenWithTrailingSeparator()
    {
        var folder = new WatchedFolder { In = Path.Combine(_dir, "customer", "In") + Path.DirectorySeparatorChar };

        Assert.Equal(Path.Combine(_dir, "customer", "Originals"), folder.OriginalsOrDefault);

        folder.Originals = Path.Combine(_dir, "elsewhere");
        Assert.Equal(Path.Combine(_dir, "elsewhere"), folder.OriginalsOrDefault);
    }

    [Theory]
    [InlineData(@"D:\", @"D:\Originals")]
    [InlineData(@"D:\In", @"D:\Originals")]
    [InlineData(@"\\srv\share", @"\\srv\share\Originals")]
    [InlineData(@"\\srv\share\", @"\\srv\share\Originals")]
    public void OriginalsOrDefault_InAtARoot_IsAFullPath(string inFolder, string expected) =>
        Assert.Equal(expected, new WatchedFolder { In = inFolder }.OriginalsOrDefault);

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
    [InlineData(0)]
    [InlineData(-5)]
    public void ConfigStore_PollSecondsBelowOne_ThrowsConfigException(int seconds)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "poll.json");
        File.WriteAllText(path, "{ \"pollSeconds\": " + seconds + " }");

        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Load(path));
        Assert.Contains("pollSeconds", ex.Message);
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

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
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
