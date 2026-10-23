using Illux.ImageTool.Ae;

namespace Illux.ImageTool.Tests;

public class InputListParserTests
{
    [Fact]
    public void Split_Semicolons()
    {
        var result = InputListParser.Split("C:\\a\\x.jpg;C:\\b\\y.tif");

        Assert.Equal(new[] { "C:\\a\\x.jpg", "C:\\b\\y.tif" }, result);
    }

    [Fact]
    public void Split_ColonSeparatedKeepsDriveLetters()
    {
        var result = InputListParser.Split("C:\\a\\x.jpg:D:\\b\\y.tif");

        Assert.Equal(new[] { "C:\\a\\x.jpg", "D:\\b\\y.tif" }, result);
    }

    [Fact]
    public void Split_UncAndDanishNames()
    {
        var result = InputListParser.Split("\\\\srv\\job 1\\Ærø.jpg;\\\\srv\\job 1\\blå.tif");

        Assert.Equal(new[] { "\\\\srv\\job 1\\Ærø.jpg", "\\\\srv\\job 1\\blå.tif" }, result);
    }

    [Fact]
    public void Split_ColonSeparatedUnc()
    {
        var result = InputListParser.Split("\\\\srv\\share\\x.jpg:\\\\srv\\share\\y.jpg");

        Assert.Equal(new[] { "\\\\srv\\share\\x.jpg", "\\\\srv\\share\\y.jpg" }, result);
    }

    [Fact]
    public void Split_Single()
    {
        var result = InputListParser.Split("C:\\a\\x.jpg");

        Assert.Equal(new[] { "C:\\a\\x.jpg" }, result);
    }

    [Fact]
    public void Split_QuotedEntries_AreUnquoted()
    {
        var result = InputListParser.Split("\"C:\\a b\\x.jpg\";  \"D:\\y.tif\" ");

        Assert.Equal(new[] { "C:\\a b\\x.jpg", "D:\\y.tif" }, result);
    }

    [Fact]
    public void Split_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(InputListParser.Split(""));
        Assert.Empty(InputListParser.Split("   "));
        Assert.Empty(InputListParser.Split(";;  ;"));
    }
}
