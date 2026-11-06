using System.Text;
using Illux.ImageTool.Pdf.Input;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Tests;

/// <summary>
/// <see cref="PdfSyntax"/> writes a token only when the text it writes means exactly what the file said (ruling P1b);
/// otherwise it refuses with <see cref="NotSupportedException"/>. Tokens are parsed from PDF text with PdfPig, written,
/// and checked as raw text.
/// </summary>
public sealed class PdfSyntaxTests
{
    private static IToken Parse(string pdf)
    {
        var scanner = new CoreTokenScanner(new MemoryInputBytes(Encoding.Latin1.GetBytes(pdf)), true, new StackDepthGuard(64));
        Assert.True(scanner.MoveNext());
        return scanner.CurrentToken;
    }

    private static string Written(string pdf) => Encoding.Latin1.GetString(PdfSyntax.ToBytes(Parse(pdf)));

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-17", "-17")]
    [InlineData("2147483647", "2147483647")]
    [InlineData("-2147483648", "-2147483648")]
    [InlineData("1.00135", "1.00135")]
    [InlineData("-.5", "-0.5")]
    [InlineData("3.", "3")]
    [InlineData("1.0", "1")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("0.00000000000000000000000000001234", "0.00000000000000000000000000001234")]
    [InlineData("1234567890123456.5", "1234567890123456.5")]
    [InlineData("0.1", "0.1")]
    public void Number_WrittenExactly_NoExponent(string input, string expected)
    {
        var written = Written(input);

        Assert.Equal(expected, written);
        Assert.Equal(((NumericToken)Parse(input)).Data, ((NumericToken)Parse(written)).Data);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("3000000000.0")] // a whole-number real beyond 32 bits must not become a huge integer
    [InlineData("9007199254740993")]
    [InlineData("12345678901234567890")]
    public void Number_OutsideIntegerRange_Refused(string input)
    {
        var ex = Assert.Throws<NotSupportedException>(() => PdfSyntax.ToBytes(Parse(input)));
        Assert.Contains("number", ex.Message);
    }

    [Theory]
    [InlineData("/Im0", "/Im0")]
    [InlineData("/A#23B#20C#2F", "/A#23B#20C#2F")]
    [InlineData("/a#28b#29", "/a#28b#29")]
    [InlineData("/", "/")]
    public void Name_Ascii_EscapedAsNeeded(string input, string expected) => Assert.Equal(expected, Written(input));

    [Theory]
    [InlineData("/F#80")]       // PdfPig reads it as Windows-1252 "F€"
    [InlineData("/caf#E9")]
    [InlineData("/caf#C3#A9")]
    [InlineData("/#82l#82r#83S#83V#83b#83N")] // a Shift-JIS font name
    public void Name_NonAscii_Refused(string input)
    {
        var ex = Assert.Throws<NotSupportedException>(() => PdfSyntax.ToBytes(Parse(input)));
        Assert.Contains("name", ex.Message);
    }

    [Theory]
    [InlineData(@"(plain text)", @"(plain text)")]
    [InlineData(@"(a\(b\)c\\d)", @"(a\(b\)c\\d)")]
    [InlineData("(a\nb)", @"(a\nb)")]
    [InlineData(@"(\200\351\000)", @"(\200\351\000)")]
    [InlineData(@"(\376\377\000)", @"(\376\377\000)")]
    [InlineData("<00FF0d>", "<00FF0D>")]
    public void String_WrittenFromItsBytes(string input, string expected) => Assert.Equal(expected, Written(input));

    [Theory]
    [InlineData("(a\rb)")]   // a raw CR in a literal means LF to a conforming reader; PdfPig reads 0D
    [InlineData("(a\r\nb)")] // a raw CR LF means one LF
    [InlineData(@"(a\rb)")]  // an escaped CR cannot be told apart from those
    public void LiteralString_WithCr_Refused(string input)
    {
        var ex = Assert.Throws<NotSupportedException>(() => PdfSyntax.ToBytes(Parse(input)));
        Assert.Contains("carriage return", ex.Message);
    }

    [Fact]
    public void Containers_WrittenEntryForEntry()
    {
        Assert.Equal("<< /A [1 0 R null true false [] << >>] /B (x) >>", Written("<</A[1 0 R null true false[]<<>>]/B(x)>>"));
    }
}
