using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Writes PdfPig tokens back as PDF syntax, for the objects the rewriter cannot copy byte for byte: replaced images
/// and objects that lived in object streams (ruling P1b). A token is written only when the text means exactly what the
/// file said; anything PdfPig's reading may have changed is refused with <see cref="NotSupportedException"/>:
/// <list type="bullet">
/// <item>a name with a character outside ASCII: PdfPig decodes name bytes as UTF-8 or Windows-1252, so the bytes
/// cannot be recovered, and a resource name must match its use in content streams byte for byte;</item>
/// <item>a literal string with a carriage return: a raw CR or CR LF means LF to a conforming reader, but PdfPig gives
/// 0D for those and for an escaped <c>\r</c> alike;</item>
/// <item>a whole number outside the 32-bit integer range (beyond 2^53 PdfPig's value is not even exact, and a large
/// whole-number real must not turn into an integer readers cannot hold);</item>
/// <item>a token that is not a PDF object (an operator, a comment) and a number that is not finite.</item>
/// </list>
/// Strings are written from their bytes (literal stays literal, hex stays hex); reals from their shortest round-trip
/// digits, never with an exponent. PdfPig's own <c>TokenWriter</c> is not used: it changes values (decision P1a).
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

    /// <summary>Writes <paramref name="token"/> (null as <c>null</c>), or throws <see cref="NotSupportedException"/>.</summary>
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
                WriteAscii(output, Name(name.Data));
                break;
            case StringToken text:
                WriteAscii(output, Literal(text.GetBytes()));
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
                    WriteAscii(output, " " + Name(key) + " ");
                    Write(value, output);
                }
                WriteAscii(output, " >>");
                break;
            default:
                throw new NotSupportedException($"a {token.GetType().Name} is not a PDF object that can be written back");
        }
    }

    /// <summary>A whole number in 32-bit range as an integer; a real from its shortest round-trip digits.</summary>
    internal static string Number(NumericToken number)
    {
        var value = number.Data;
        if (!double.IsFinite(value)) throw new NotSupportedException("a number that is not finite cannot be written back");
        if (!number.HasDecimalPlaces)
        {
            if (value is < int.MinValue or > int.MaxValue)
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                    $"the number {value:R} is outside the 32-bit integer range and cannot be written back exactly"));
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        var text = PlainDecimal(value.ToString("R", CultureInfo.InvariantCulture));
        if (!text.Contains('.', StringComparison.Ordinal)) text += ".0";
        if (double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) != value)
            throw new NotSupportedException($"the number {text} cannot be written back exactly");
        return text;
    }

    /// <summary>"1.234E-29" → "0.00000000000000000000000000001234": the same digits with the decimal point moved.</summary>
    private static string PlainDecimal(string roundTrip)
    {
        var e = roundTrip.IndexOfAny(['E', 'e']);
        if (e < 0) return roundTrip;
        var exponent = int.Parse(roundTrip[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = roundTrip[..e];
        var sign = mantissa.StartsWith('-') ? "-" : "";
        mantissa = mantissa.TrimStart('-');
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var integerDigits = (dot < 0 ? mantissa.Length : dot) + exponent;
        var plain = integerDigits <= 0 ? "0." + new string('0', -integerDigits) + digits
            : integerDigits >= digits.Length ? digits + new string('0', integerDigits - digits.Length)
            : digits[..integerDigits] + "." + digits[integerDigits..];
        return sign + plain;
    }

    /// <summary>An ASCII name with every byte outside <c>!</c>..<c>~</c>, <c>#</c> and the delimiters as <c>#xx</c>.</summary>
    private static string Name(string name)
    {
        var text = new StringBuilder("/");
        foreach (var c in name)
        {
            if (c >= 0x80)
                throw new NotSupportedException($"the name /{name} has non-ASCII bytes that cannot be written back exactly");
            if (c is < '!' or > '~' or '#' || "()<>[]{}/%".Contains(c, StringComparison.Ordinal))
                text.Append('#').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            else
                text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>A literal string of exactly these bytes, in printable ASCII (escapes for the rest).</summary>
    private static string Literal(byte[] bytes)
    {
        if (bytes.Contains((byte)'\r'))
            throw new NotSupportedException("a literal string with a carriage return cannot be written back exactly");
        var text = new StringBuilder("(", bytes.Length + 2);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(': text.Append(@"\("); break;
                case (byte)')': text.Append(@"\)"); break;
                case (byte)'\\': text.Append(@"\\"); break;
                case (byte)'\n': text.Append(@"\n"); break;
                case (byte)'\t': text.Append(@"\t"); break;
                case < 0x20 or > 0x7E: text.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0')); break;
                default: text.Append((char)b); break;
            }
        }
        return text.Append(')').ToString();
    }

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}
