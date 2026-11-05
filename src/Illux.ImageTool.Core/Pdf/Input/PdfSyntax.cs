using System.Globalization;
using System.Text;
using System.Text.Unicode;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Writes PdfPig tokens back as PDF syntax so that a reader gets the same values (decision P1). PdfPig 0.1.16's own
/// <c>TokenWriter</c> is not used for this because it changes values: integers beyond 32 bits become 2147483647 (or
/// nothing), a literal string with PDFDocEncoding bytes such as <c>\200</c> is rewritten as UTF-16, and <c>#</c> in a
/// name is not escaped. Here strings are written from their bytes (literal stays literal, hex stays hex), integers in
/// full, reals without exponent, and names with every byte outside <c>!</c>..<c>~</c>, <c>#</c> and the delimiters as
/// <c>#xx</c>.
/// </summary>
internal static class PdfSyntax
{
    /// <summary>The token as PDF syntax.</summary>
    public static byte[] ToBytes(IToken token)
    {
        using var output = new MemoryStream();
        Write(token, output);
        return output.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="token"/> (null as <c>null</c>). Throws <see cref="NotSupportedException"/> for a token
    /// that is not a PDF object (an operator, a comment) and for a non-ASCII name whose bytes cannot be told (see
    /// <see cref="NameBytes"/>).
    /// </summary>
    public static void Write(IToken? token, Stream output)
    {
        switch (token)
        {
            case null or NullToken:
                WriteAscii(output, "null");
                break;
            case BooleanToken boolean:
                WriteAscii(output, boolean.Data ? "true" : "false");
                break;
            case NumericToken number:
                WriteAscii(output, Number(number));
                break;
            case NameToken name:
                Name(output, name.Data);
                break;
            case StringToken text:
                Literal(output, text.GetBytes());
                break;
            case HexToken hex:
                WriteAscii(output, "<" + Convert.ToHexString(hex.Bytes) + ">");
                break;
            case IndirectReferenceToken reference:
                WriteAscii(output, string.Create(CultureInfo.InvariantCulture,
                    $"{reference.Data.ObjectNumber} {reference.Data.Generation} R"));
                break;
            case ArrayToken array:
                output.WriteByte((byte)'[');
                for (var i = 0; i < array.Data.Count; i++)
                {
                    if (i > 0) output.WriteByte((byte)' ');
                    Write(array.Data[i], output);
                }
                output.WriteByte((byte)']');
                break;
            case DictionaryToken dictionary:
                WriteAscii(output, "<<");
                foreach (var (key, value) in dictionary.Data)
                {
                    output.WriteByte((byte)' ');
                    Name(output, key);
                    output.WriteByte((byte)' ');
                    Write(value, output);
                }
                WriteAscii(output, " >>");
                break;
            default:
                throw new NotSupportedException($"A {token.GetType().Name} cannot be written as a PDF object.");
        }
    }

    /// <summary>An integer in full; a real in its shortest round-trip form, never with an exponent.</summary>
    internal static string Number(NumericToken number)
    {
        var value = number.Data;
        if (!double.IsFinite(value)) throw new InvalidDataException("A PDF number is not finite.");
        if (!number.HasDecimalPlaces)
            return Math.Abs(value) < 9e18 ? number.Long.ToString(CultureInfo.InvariantCulture) : value.ToString("0", CultureInfo.InvariantCulture);
        if (value == 0) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E', StringComparison.Ordinal)
            ? value.ToString("0.############################", CultureInfo.InvariantCulture)
            : text;
    }

    /// <summary>
    /// The bytes of a name as they were in the file. PdfPig decodes a name's bytes as UTF-8 when they are valid UTF-8
    /// and as Latin-1 otherwise, so: ASCII is ASCII; a character above U+00FF can only come from UTF-8; Latin-1 bytes
    /// that are valid UTF-8 cannot be the source (they would have been decoded as UTF-8), so it was UTF-8. Otherwise
    /// (for example <c>café</c> from <c>caf#E9</c> or from <c>caf#C3#A9</c>) the bytes cannot be told, and the name is
    /// refused rather than possibly changed: a resource name must match its use in content streams byte for byte.
    /// </summary>
    internal static byte[] NameBytes(string name)
    {
        if (System.Text.Ascii.IsValid(name)) return Encoding.ASCII.GetBytes(name);
        if (name.Any(c => c > 0xFF)) return Encoding.UTF8.GetBytes(name);
        var latin1 = Encoding.Latin1.GetBytes(name);
        if (Utf8.IsValid(latin1)) return Encoding.UTF8.GetBytes(name);
        throw new NotSupportedException($"The PDF name /{name} has non-ASCII characters whose bytes cannot be told.");
    }

    private static void Name(Stream output, string name)
    {
        var text = new StringBuilder("/");
        foreach (var b in NameBytes(name))
        {
            if (b is < 0x21 or > 0x7E or (byte)'#' || "()<>[]{}/%".Contains((char)b, StringComparison.Ordinal))
                text.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else
                text.Append((char)b);
        }
        WriteAscii(output, text.ToString());
    }

    /// <summary>A literal string of exactly these bytes, in printable ASCII (escapes for the rest).</summary>
    private static void Literal(Stream output, byte[] bytes)
    {
        var text = new StringBuilder("(", bytes.Length + 2);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(': text.Append(@"\("); break;
                case (byte)')': text.Append(@"\)"); break;
                case (byte)'\\': text.Append(@"\\"); break;
                case (byte)'\n': text.Append(@"\n"); break;
                case (byte)'\r': text.Append(@"\r"); break;
                case (byte)'\t': text.Append(@"\t"); break;
                case < 0x20 or > 0x7E: text.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0')); break;
                default: text.Append((char)b); break;
            }
        }
        WriteAscii(output, text.Append(')').ToString());
    }

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}
