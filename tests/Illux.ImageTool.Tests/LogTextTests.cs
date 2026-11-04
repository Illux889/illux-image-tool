using Illux.ImageTool.Batch;
using Illux.ImageTool.Options;
using Illux.ImageTool.Runtime;
using Illux.ImageTool.Watcher.Logging;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Final review C1 (both systems): a file name can carry control characters (a line break on macOS), which must not
/// forge or split a line in any log the watcher or the command-line tool writes.
/// </summary>
public sealed class LogTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-logtext-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("a.jpg", "a.jpg")]
    [InlineData("a\nERROR: forged.jpg", "aERROR: forged.jpg")]
    [InlineData("a\r\n\t\u001b[31mb\u0085c\u0000.jpg", "a[31mbc.jpg")]
    [InlineData("Æble ø.jpg", "Æble ø.jpg")]
    public void Clean_DropsControlCharactersOnly(string text, string expected) =>
        Assert.Equal(expected, LogText.Clean(text));

    [Fact]
    public async Task Cli_FileNameWithALineBreak_IsOneLineInTheTaskLog()
    {
        var log = new StringWriter();
        var input = Path.Combine(_dir, "in", "a\nERROR: forged.jpg"); // need not exist: its line is written all the same

        await BatchRunner.RunAsync([input], Path.Combine(_dir, "out"), ProcessingOptions.Default, log, CancellationToken.None);

        var lines = log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("ERROR: aERROR: forged.jpg: ", lines[0]);
        Assert.StartsWith("Result: Error", lines[1]);
    }

    [Fact]
    public void ServiceLog_MessageWithALineBreak_IsOneLine()
    {
        using (var provider = new FileLoggerProvider(() => _dir))
            provider.CreateLogger("Test").LogWarning("'{File}' done", "a\n2026-01-01 00:00:00.000 [ERR] forged");

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_dir)));
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("'a2026-01-01 00:00:00.000 [ERR] forged' done", text);
    }
}
