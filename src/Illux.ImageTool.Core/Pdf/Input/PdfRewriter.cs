using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.CrossReference;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Writes a PDF again in full with some image streams replaced (decision P1): PdfPig reads every object the input's
/// cross-reference data lists, and each is written as a plain object (<see cref="PdfSyntax"/>), followed by one classic
/// cross-reference table and trailer.
/// <list type="bullet">
/// <item>The header is the input's own <c>%PDF-x.y</c> (never lowered); the catalog, and with it any <c>/Version</c>,
/// is copied like every other object.</item>
/// <item>Object streams and cross-reference streams are not written (their objects are written as plain objects), nor
/// a linearization dictionary (its offsets would be wrong). Their numbers become free entries of a linked free list.</item>
/// <item>Every stream gets a direct <c>/Length</c> equal to the data written, so a wrong indirect length that PdfPig
/// repaired on reading is not copied.</item>
/// <item>The trailer has <c>/Size</c>, <c>/Root</c>, <c>/Info</c> and <c>/ID</c> from the input; nothing else
/// (no <c>/Prev</c>, <c>/XRefStm</c>, <c>/Encrypt</c>).</item>
/// </list>
/// The input is read from the file as needed (not loaded whole), and object data is written straight to the output.
/// </summary>
public static class PdfRewriter
{
    /// <summary>
    /// Writes <paramref name="outputPdf"/>: <paramref name="inputPdf"/> with exactly the objects in
    /// <paramref name="replacements"/> given new dictionaries and data (each dictionary is PDF syntax, as
    /// <see cref="PdfImageEncoder"/> writes it; its <c>/Length</c> is replaced by the data length). On any exception no
    /// file is left at <paramref name="outputPdf"/>. Throws <see cref="ArgumentException"/> when a replaced object is
    /// not in the input or the output path is the input's.
    /// </summary>
    public static void Write(string inputPdf, string outputPdf, IReadOnlyDictionary<PdfObjectId, PdfImageStream> replacements)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPdf);
        ArgumentException.ThrowIfNullOrEmpty(outputPdf);
        ArgumentNullException.ThrowIfNull(replacements);
        if (string.Equals(Path.GetFullPath(inputPdf), Path.GetFullPath(outputPdf), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output PDF must not be the input PDF.", nameof(outputPdf));

        try
        {
            var streams = replacements.ToDictionary(
                r => new IndirectReference(r.Key.Number, r.Key.Generation),
                r => ToStream(r.Value ?? throw new ArgumentException($"Object {r.Key.Number} {r.Key.Generation} R has no replacement.", nameof(replacements))));

            using var input = File.OpenRead(inputPdf);
            var version = PdfImageScanner.HeaderVersion(input) ?? throw new InvalidDataException("The file has no %PDF header.");
            input.Position = 0;
            using var document = PdfDocument.Open(input, new ParsingOptions { SkipMissingFonts = true });
            if (document.IsEncrypted) throw new NotSupportedException("encrypted PDFs are not supported");
            var structure = document.Structure;

            // One generation per number: the newest, if a damaged table lists several.
            var references = structure.CrossReferenceTable.ObjectOffsets.Keys
                .GroupBy(r => r.ObjectNumber)
                .Select(g => g.MaxBy(r => r.Generation))
                .Where(r => r.ObjectNumber > 0)
                .OrderBy(r => r.ObjectNumber)
                .ToList();
            foreach (var reference in streams.Keys.Where(r => !references.Contains(r)))
                throw new ArgumentException($"The PDF has no object {reference.ObjectNumber} {reference.Generation} R to replace.", nameof(replacements));

            using var output = new FileStream(outputPdf, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            new PdfObjectWriter(output).WriteHeader(version);

            var written = new SortedDictionary<long, (long Offset, int Generation)>();
            foreach (var reference in references)
            {
                var token = streams.TryGetValue(reference, out var replacement) ? replacement : Read(structure, reference);
                if (IsLeftOut(token)) continue;
                written[reference.ObjectNumber] = (output.Position, reference.Generation);
                WriteObject(output, reference, token);
            }
            WriteTrailer(output, written, structure.Trailer);
        }
        catch
        {
            // Also a file that was there before: it would have been replaced, and no output may look written.
            TryDelete(outputPdf);
            throw;
        }
    }

    /// <summary>The replacement as a stream token: exactly one dictionary, with a direct <c>/Length</c> of the data.</summary>
    private static StreamToken ToStream(PdfImageStream replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement.Dictionary);
        ArgumentNullException.ThrowIfNull(replacement.Data);
        var scanner = new CoreTokenScanner(new MemoryInputBytes(replacement.Dictionary), true, new StackDepthGuard(64));
        if (!scanner.MoveNext() || scanner.CurrentToken is not DictionaryToken dictionary || scanner.MoveNext())
            throw new ArgumentException("A replacement image dictionary is not exactly one PDF dictionary.", nameof(replacement));
        return new StreamToken(dictionary.With("Length", new NumericToken(replacement.Data.Length)), replacement.Data);
    }

    /// <summary>
    /// The object as PdfPig reads it. One it cannot read (even one nothing refers to) fails the rewrite: it cannot be
    /// copied, and leaving it out could change what another reader shows.
    /// </summary>
    private static IToken Read(Structure structure, IndirectReference reference)
    {
        try
        {
            return structure.GetObject(reference).Data;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException(
                $"object {reference.ObjectNumber} {reference.Generation} R could not be read ({PdfImageScanner.FirstLine(ex.Message)})", ex);
        }
    }

    /// <summary>Object and cross-reference streams (replaced by the classic table) and a linearization dictionary.</summary>
    private static bool IsLeftOut(IToken token) => token switch
    {
        StreamToken stream => stream.StreamDictionary.Data.TryGetValue("Type", out var type) && type is NameToken { Data: "ObjStm" or "XRef" },
        DictionaryToken dictionary => dictionary.Data.ContainsKey("Linearized"),
        _ => false,
    };

    private static void WriteObject(Stream output, IndirectReference reference, IToken token)
    {
        Ascii(output, string.Create(CultureInfo.InvariantCulture, $"{reference.ObjectNumber} {reference.Generation} obj\n"));
        if (token is StreamToken stream)
        {
            PdfSyntax.Write(stream.StreamDictionary.With("Length", new NumericToken(stream.Data.Length)), output);
            Ascii(output, "\nstream\n");
            output.Write(stream.Data.Span);
            Ascii(output, "\nendstream");
        }
        else
        {
            PdfSyntax.Write(token, output);
        }
        Ascii(output, "\nendobj\n");
    }

    /// <summary>
    /// One table from object 0 to the highest number written, 20-byte entries; every number not written is free, and
    /// entry 0 starts the list that links them in ascending order and ends in 0.
    /// </summary>
    private static void WriteTrailer(Stream output, SortedDictionary<long, (long Offset, int Generation)> written, TrailerDictionary trailer)
    {
        var size = written.Count == 0 ? 1 : checked((int)written.Keys.Max() + 1);
        var free = Enumerable.Range(1, size - 1).Where(n => !written.ContainsKey(n)).ToList();
        var next = free.Append(0).Skip(1).ToList(); // next[i] = the free number after free[i], 0 for the last

        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {size}\n");
        table.Append(CultureInfo.InvariantCulture, $"{(free.Count > 0 ? free[0] : 0):D10} 65535 f\r\n");
        var freeIndex = 0;
        for (var number = 1; number < size; number++)
        {
            if (written.TryGetValue(number, out var entry))
                table.Append(CultureInfo.InvariantCulture, $"{entry.Offset:D10} {entry.Generation:D5} n\r\n");
            else
                table.Append(CultureInfo.InvariantCulture, $"{next[freeIndex++]:D10} 00000 f\r\n");
        }
        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {size} /Root {trailer.Root.ObjectNumber} {trailer.Root.Generation} R");
        Ascii(output, table.ToString());
        if (trailer.Info is { } info)
        {
            Ascii(output, " /Info ");
            PdfSyntax.Write(info, output);
        }
        if (trailer.Identifier is { Count: > 0 } identifier)
        {
            Ascii(output, " /ID ");
            PdfSyntax.Write(new ArrayToken(identifier.Cast<IToken>().ToList()), output);
        }
        Ascii(output, string.Create(CultureInfo.InvariantCulture, $" >>\nstartxref\n{xref}\n%%EOF\n"));
    }

    private static void Ascii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
