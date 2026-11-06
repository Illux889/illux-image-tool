namespace Illux.ImageTool.Tests;

/// <summary>Checks on the Markdown documents that ship with the tool.</summary>
public sealed class DocsTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IlluxImageTool.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }

    [Theory]
    [InlineData("docs", "ae-setup.md")]
    [InlineData("README.md")]
    [InlineData("docs", "release-notes", "v0.3.0.md")]
    public void Markdown_TablesAreNotBrokenByABlankLine(params string[] path)
    {
        // A blank line or paragraph inside a table ends it; the rows after it then show as raw text.
        var lines = File.ReadAllLines(RepoFile(path));
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith('|')) continue;
            var isHeader = i + 1 < lines.Length && lines[i + 1].StartsWith("|---", StringComparison.Ordinal);
            if (isHeader) continue;
            Assert.True(i > 0 && lines[i - 1].StartsWith('|'), $"{string.Join('/', path)} line {i + 1} is a table row outside a table: {lines[i]}");
        }
    }
}
