using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Tests;

/// <summary>
/// The libc helpers the macOS folders job and the root job use on folders others may write to: never through a link,
/// never blocking on a FIFO, never writing into someone else's file (final review C1, C2, I1).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class UnixHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-unix-" + Guid.NewGuid().ToString("N"));

    public UnixHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static uint Me => UnixNative.EffectiveUserId;

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ushort mode);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int HardLink([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);

    private static T Within<T>(Func<T> action, string what)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), what + " blocked");
        return task.Result;
    }

    // ----- the trusted-folder check shared by the control socket, the update folder and the config save -----

    [MacFact]
    public void TrustedFolder_OwnFolderClosedToOthers_IsTrusted()
    {
        var folder = P("ok");
        Directory.CreateDirectory(folder, (UnixFileMode)0b111_101_101);

        Assert.Null(TrustedFolder.Problem(folder, Me));
    }

    [MacFact]
    public void TrustedFolder_Link_IsNotTrusted()
    {
        Directory.CreateDirectory(P("target"), (UnixFileMode)0b111_000_000);
        Directory.CreateSymbolicLink(P("link"), P("target"));

        Assert.Equal("is a link", TrustedFolder.Problem(P("link"), Me));
    }

    [MacFact]
    public void TrustedFolder_WritableByGroupOrOthers_IsNotTrusted()
    {
        Directory.CreateDirectory(P("open"));
        File.SetUnixFileMode(P("open"), (UnixFileMode)0b111_111_111);

        Assert.Equal("is writable by group or others", TrustedFolder.Problem(P("open"), Me));
    }

    [MacFact]
    public void TrustedFolder_SomeoneElses_IsNotTrusted()
    {
        Directory.CreateDirectory(P("theirs"), (UnixFileMode)0b111_000_000);

        Assert.Equal($"is owned by user {Me}, not {Me + 1}", TrustedFolder.Problem(P("theirs"), Me + 1));
    }

    [MacFact]
    public void TrustedFolder_MissingOrAFile_IsNotTrusted()
    {
        File.WriteAllText(P("file"), "x");

        Assert.Equal("does not exist", TrustedFolder.Problem(P("missing"), Me));
        Assert.Equal("is not a folder", TrustedFolder.Problem(P("file"), Me));
    }

    // ----- C1: appending to a log in a folder others may write to -----

    [MacFact]
    public void AppendToOwnFile_CreatesThenAppends()
    {
        var log = P("_warnings.log");

        Assert.Null(UnixNative.AppendToOwnFile(log, Encoding.UTF8.GetBytes("one\n")));
        Assert.Null(UnixNative.AppendToOwnFile(log, Encoding.UTF8.GetBytes("two\n")));

        Assert.Equal("one\ntwo\n", File.ReadAllText(log));
        Assert.Equal((UnixFileMode)0b110_100_100, File.GetUnixFileMode(log) & (UnixFileMode)0b111_111_111);
    }

    [MacFact]
    public void AppendToOwnFile_SymbolicLink_IsNotWrittenThrough()
    {
        File.WriteAllText(P("target"), "keep");
        File.CreateSymbolicLink(P("_warnings.log"), P("target"));

        Assert.NotNull(UnixNative.AppendToOwnFile(P("_warnings.log"), Encoding.UTF8.GetBytes("planted\n")));

        Assert.Equal("keep", File.ReadAllText(P("target")));
    }

    [MacFact]
    public void AppendToOwnFile_HardLink_IsNotWrittenThrough()
    {
        // A second name for another file (as a user may plant in place of _warnings.log): link count 2.
        File.WriteAllText(P("target"), "keep");
        Assert.Equal(0, HardLink(P("target"), P("_warnings.log")));

        var reason = UnixNative.AppendToOwnFile(P("_warnings.log"), Encoding.UTF8.GetBytes("planted\n"));

        Assert.Contains("more than one name", reason);
        Assert.Equal("keep", File.ReadAllText(P("target")));
    }

    [MacFact]
    public void AppendToOwnFile_Fifo_NeverBlocks()
    {
        Assert.Equal(0, MakeFifo(P("_warnings.log"), 0b110_110_110));

        Assert.NotNull(Within(() => UnixNative.AppendToOwnFile(P("_warnings.log"), Encoding.UTF8.GetBytes("x\n")), "the append"));
        Assert.Equal(UnixNative.FifoType, UnixNative.LStat(P("_warnings.log"))!.Value.Type);
    }

    // ----- I1: the "can it be opened exclusively" probe on files in In -----

    [MacFact]
    public void CanLockExclusively_RegularFile_UntilSomeoneHoldsIt()
    {
        File.WriteAllText(P("a.jpg"), "x");

        Assert.True(UnixNative.CanLockExclusively(P("a.jpg")));
        using (new FileStream(P("a.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(UnixNative.CanLockExclusively(P("a.jpg")));
        Assert.True(UnixNative.CanLockExclusively(P("a.jpg")));
    }

    [MacFact]
    public void CanLockExclusively_FifoOrLink_IsFalse_AndNeverBlocks()
    {
        Assert.Equal(0, MakeFifo(P("pipe.jpg"), 0b110_110_110));
        File.WriteAllText(P("target.jpg"), "x");
        File.CreateSymbolicLink(P("link.jpg"), P("target.jpg"));

        Assert.False(Within(() => UnixNative.CanLockExclusively(P("pipe.jpg")), "the probe"));
        Assert.False(UnixNative.CanLockExclusively(P("link.jpg")));
    }

    // ----- C2: moving a staged output into Out -----

    [MacFact]
    public void MoveExclusive_FreeName_Moves()
    {
        File.WriteAllText(P("staged.jpg"), "output");

        UnixNative.MoveExclusive(P("staged.jpg"), P("out.jpg"));

        Assert.Equal("output", File.ReadAllText(P("out.jpg")));
        Assert.False(File.Exists(P("staged.jpg")));
    }

    [MacFact]
    public void MoveExclusive_NameTaken_ByAFileOrALink_Throws_NothingReplaced()
    {
        File.WriteAllText(P("staged.jpg"), "output");
        File.WriteAllText(P("taken.jpg"), "someone's");
        File.WriteAllText(P("target"), "keep");
        File.CreateSymbolicLink(P("link.jpg"), P("target"));
        File.CreateSymbolicLink(P("dangling.jpg"), P("nowhere"));

        foreach (var name in new[] { "taken.jpg", "link.jpg", "dangling.jpg" })
        {
            var ex = Assert.Throws<IOException>(() => UnixNative.MoveExclusive(P("staged.jpg"), P(name)));
            Assert.True(UnixNative.IsAlreadyExists(ex), name);
        }

        Assert.Equal("someone's", File.ReadAllText(P("taken.jpg")));
        Assert.Equal("keep", File.ReadAllText(P("target")));
        Assert.False(File.Exists(P("nowhere")));
        Assert.Equal("output", File.ReadAllText(P("staged.jpg")));
    }

    [MacFact]
    public void CopyExclusive_TheOtherVolumeFallback_NeverWritesThroughALink()
    {
        File.WriteAllText(P("staged.jpg"), "output");
        File.WriteAllText(P("target"), "keep");
        File.CreateSymbolicLink(P("link.jpg"), P("target"));
        File.CreateSymbolicLink(P("dangling.jpg"), P("nowhere"));

        Assert.Throws<IOException>(() => UnixNative.CopyExclusive(P("staged.jpg"), P("link.jpg")));
        Assert.Throws<IOException>(() => UnixNative.CopyExclusive(P("staged.jpg"), P("dangling.jpg")));
        UnixNative.CopyExclusive(P("staged.jpg"), P("out.jpg"));

        Assert.Equal("keep", File.ReadAllText(P("target")));
        Assert.False(File.Exists(P("nowhere")));
        Assert.Equal("output", File.ReadAllText(P("out.jpg")));
        Assert.False(File.Exists(P("staged.jpg")));
    }
}
