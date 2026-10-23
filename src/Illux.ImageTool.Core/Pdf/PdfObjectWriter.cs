using System.Globalization;
using System.Text;

namespace Illux.ImageTool.Pdf;

/// <summary>
/// Low-level PDF file syntax: header, numbered objects, streams, classic cross-reference table and trailer.
/// Object bodies are passed in as PDF source text; this class only tracks byte offsets.
/// </summary>
internal sealed class PdfObjectWriter(Stream output)
{
    // Index = object number; entry 0 is the head of the free list. -1 = reserved but not written yet.
    private readonly List<long> _offsets = [0];

    /// <summary>Writes the header and a comment line of high bytes (so transfer tools treat the file as binary).</summary>
    public void WriteHeader(string version)
    {
        WriteText($"%PDF-{version}\n");
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    /// <summary>Reserves the next object number, so objects can refer to each other before they are written.</summary>
    public int Reserve()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    public void WriteObject(int number, string body)
    {
        BeginObject(number);
        WriteText(body);
        WriteText("\nendobj\n");
    }

    /// <summary>
    /// Writes a stream object. <paramref name="dictionaryEntries"/> must not contain <c>/Length</c>: the length is
    /// written as a separate object afterwards, so <paramref name="writeData"/> can stream data of unknown size.
    /// </summary>
    public void WriteStream(int number, string dictionaryEntries, Action<Stream> writeData)
    {
        var lengthNumber = Reserve();
        BeginObject(number);
        var entries = dictionaryEntries.Length > 0 ? dictionaryEntries + " " : "";
        WriteText($"<< {entries}/Length {lengthNumber} 0 R >>\nstream\n");
        var start = output.Position;
        writeData(output);
        var length = output.Position - start;
        WriteText("\nendstream\nendobj\n");
        WriteObject(lengthNumber, length.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes the cross-reference table and trailer; every reserved object must have been written.</summary>
    public void WriteTrailer(int root, int info)
    {
        if (_offsets.Contains(-1)) throw new InvalidOperationException("A reserved PDF object was never written.");

        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {_offsets.Count}\n");
        table.Append("0000000000 65535 f\r\n"); // each entry is exactly 20 bytes
        foreach (var offset in _offsets.Skip(1))
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {_offsets.Count} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        WriteText(table.ToString());
    }

    /// <summary>Formats a number for PDF: invariant culture, no exponent, at most four decimals.</summary>
    public static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A PDF literal string, with the characters that need it escaped.</summary>
    public static string LiteralString(string value) =>
        "(" + value.Replace(@"\", @"\\").Replace("(", @"\(").Replace(")", @"\)") + ")";

    private void BeginObject(int number)
    {
        if (number <= 0 || number >= _offsets.Count || _offsets[number] != -1)
            throw new InvalidOperationException($"PDF object {number} was not reserved or is already written.");
        _offsets[number] = output.Position;
        WriteText($"{number} 0 obj\n");
    }

    private void WriteText(string text) => output.Write(Encoding.Latin1.GetBytes(text));
}
