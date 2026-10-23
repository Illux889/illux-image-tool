using Illux.ImageTool.Options;

namespace Illux.ImageTool.Tests;

public class ParameterParserTests
{
    [Fact]
    public void Parse_Empty_IsAllDefaults()
    {
        Assert.Equal(ProcessingOptions.Default, ParameterParser.Parse([]));
    }

    [Fact]
    public void Parse_BlankEntries_AreIgnored()
    {
        Assert.Equal(ProcessingOptions.Default, ParameterParser.Parse(["", "  "]));
    }

    [Fact]
    public void Parse_OnlyOutputPdf_KeepsAllOperations()
    {
        var o = ParameterParser.Parse(["output=pdf"]);
        Assert.Equal(Operations.All, o.Operations);
        Assert.Equal(OutputMode.Pdf, o.Output);
    }

    [Fact]
    public void Parse_8bitOnly()
    {
        Assert.Equal(Operations.EightBit, ParameterParser.Parse(["8bit"]).Operations);
    }

    [Fact]
    public void Parse_UpsampleWithValue()
    {
        var o = ParameterParser.Parse(["Upsample=300"]);
        Assert.Equal(Operations.Upsample, o.Operations);
        Assert.Equal(300, o.TargetPpi);
    }

    [Fact]
    public void Parse_Combination()
    {
        var o = ParameterParser.Parse(["8bit", "baseline", "output=both", "quality=90", "missingppi=skip", "filter=mitchell"]);
        Assert.Equal(Operations.EightBit | Operations.Baseline, o.Operations);
        Assert.Equal(OutputMode.Both, o.Output);
        Assert.Equal(90, o.JpegQuality);
        Assert.Equal(MissingPpiPolicy.Skip, o.MissingPpi);
        Assert.Equal(ResampleFilter.Mitchell, o.Filter);
    }

    [Fact]
    public void Parse_MissingPpi72_IsAssume72()
    {
        Assert.Equal(MissingPpiPolicy.Assume72, ParameterParser.Parse(["missingppi=72"]).MissingPpi);
    }

    [Fact]
    public void Parse_MissingPpi_BadValue_ListsDocumentedValues()
    {
        var ex = Assert.Throws<ParameterException>(() => ParameterParser.Parse(["missingppi=96"]));
        Assert.Contains("expected one of 72, skip, error", ex.Message);
    }

    [Fact]
    public void Parse_Unknown_Throws()
    {
        var ex = Assert.Throws<ParameterException>(() => ParameterParser.Parse(["8bitt"]));
        Assert.Contains("8bitt", ex.Message);
    }

    [Theory]
    [InlineData("upsample=0")]
    [InlineData("upsample=abc")]
    [InlineData("upsample=2401")]
    [InlineData("quality=101")]
    [InlineData("quality=0")]
    [InlineData("output=png")]
    [InlineData("missingppi=maybe")]
    [InlineData("filter=nearest")]
    [InlineData("output=same,pdf")]
    [InlineData("output=1,2")]
    [InlineData("output=1")]
    [InlineData("filter=mitchell,bicubic")]
    public void Parse_BadValues_Throw(string parameter)
    {
        Assert.Throws<ParameterException>(() => ParameterParser.Parse([parameter]));
    }

    [Fact]
    public void Parse_EnumValues_AreCaseInsensitive()
    {
        Assert.Equal(OutputMode.Pdf, ParameterParser.Parse(["OUTPUT=PDF"]).Output);
    }

    [Fact]
    public void Worst_ReturnsHighest()
    {
        Assert.Equal(Status.Warning, Status.Ok.Worst(Status.Warning));
        Assert.Equal(Status.Error, Status.Error.Worst(Status.Warning));
    }
}
