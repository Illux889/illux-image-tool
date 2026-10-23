using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Options;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Watch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Illux.ImageTool.Tests;

/// <summary>R44: the watcher (SYSTEM) never writes through a link a user may have put in its way.</summary>
public sealed class LinkGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-links-" + Guid.NewGuid().ToString("N"));

    public LinkGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Elsewhere()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "precious.txt"), "do not touch");
        return elsewhere;
    }

    [Fact]
    public void RealFolderAndMissingPath_AreNotLinks()
    {
        LinkGuard.ThrowIfLinkedFolder(_dir);
        LinkGuard.ThrowIfLinkedFolder(Path.Combine(_dir, "missing"));
        LinkGuard.ThrowIfLinkedFile(Path.Combine(_dir, "missing.txt"));
        Assert.False(LinkGuard.IsLink(_dir));
    }

    [FactWhenJunctionsWork]
    public void Junction_IsRefused()
    {
        var link = Path.Combine(_dir, "link");
        Assert.True(FactWhenJunctionsWorkAttribute.TryCreateJunction(link, Elsewhere()));

        Assert.True(LinkGuard.IsLink(link));
        Assert.Throws<IOException>(() => LinkGuard.ThrowIfLinkedFolder(link));
    }

    [FactWhenFileSymlinksWork]
    public async Task FileProcessor_TargetIsASymbolicLink_ErrorAndLinkTargetUntouched()
    {
        var elsewhere = Elsewhere();
        var input = TestImages.Write(Path.Combine(_dir, "in"), "a.jpg", 60, 40, ColorSpace.sRGB, 8, 72);
        var output = Path.Combine(_dir, "out");
        Directory.CreateDirectory(output);
        File.CreateSymbolicLink(Path.Combine(output, "a.jpg"), Path.Combine(elsewhere, "precious.txt"));

        var result = await FileProcessor.ProcessAsync(input, output, ProcessingOptions.Default, NamePolicy.Overwrite,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

        Assert.Equal(Status.Error, result.Status);
        Assert.Contains(result.Messages, m => m.Contains("is a link"));
        Assert.True(LinkGuard.IsLink(Path.Combine(output, "a.jpg")));
        Assert.Equal("do not touch", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }

    [FactWhenJunctionsWork]
    public async Task Watcher_ConfiguredOutFolderMayBeALink()
    {
        // The administrator chose the path; a junction there (for example to another disk) keeps working.
        var realOut = Path.Combine(_dir, "real-out");
        Directory.CreateDirectory(realOut);
        var outLink = Path.Combine(_dir, "Out");
        Assert.True(FactWhenJunctionsWorkAttribute.TryCreateJunction(outLink, realOut));
        var folder = new WatchedFolder { Name = "t", In = Path.Combine(_dir, "In"), Out = outLink, Error = Path.Combine(_dir, "Error") };
        TestImages.Write(folder.In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 300);
        var scanner = new FolderScanner(folder, NullLogger.Instance, isUpdating: () => false);

        await scanner.ScanOnceAsync(CancellationToken.None);
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(realOut, "a.jpg")));
    }

    [FactWhenFileSymlinksWork]
    public async Task Watcher_WarningsLogThatIsALink_IsNotWrittenThrough()
    {
        var elsewhere = Elsewhere();
        var folder = new WatchedFolder { Name = "t", In = Path.Combine(_dir, "In"), Out = Path.Combine(_dir, "Out"), Error = Path.Combine(_dir, "Error") };
        Directory.CreateDirectory(folder.Out);
        File.CreateSymbolicLink(Path.Combine(folder.Out, "_warnings.log"), Path.Combine(elsewhere, "precious.txt"));
        TestImages.Write(folder.In, "nores.jpg", 60, 40, ColorSpace.sRGB, 8, null); // no resolution: a Warning
        var scanner = new FolderScanner(folder, NullLogger.Instance, isUpdating: () => false);

        await scanner.ScanOnceAsync(CancellationToken.None);
        await scanner.ScanOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(folder.Out, "nores.jpg"))); // the file itself is done
        Assert.Equal("do not touch", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
    }
}
