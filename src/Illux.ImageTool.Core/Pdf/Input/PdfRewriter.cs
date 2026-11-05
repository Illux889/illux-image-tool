using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.CrossReference;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Writes a PDF again in full with some image streams replaced (rulings P1, P1b). PdfPig reads the input's
/// cross-reference data and objects; every object is then written as a plain object, followed by one classic
/// cross-reference table and trailer:
/// <list type="bullet">
/// <item>an unchanged object stored plainly in the input is copied byte for byte from <c>n g obj</c> through
/// <c>endobj</c> (<see cref="PdfRawObjects"/>), indirect <c>/Length</c> and all;</item>
/// <item>a replaced image and an object that lived in an object stream are written again with
/// <see cref="PdfSyntax"/>, which refuses anything it cannot write exactly; a written stream gets a direct
/// <c>/Length</c>;</item>
/// <item>object streams, cross-reference streams and a linearization dictionary are left out (their numbers become
/// free entries for the next generation);</item>
/// <item>the header is the input's own <c>%PDF-x.y</c> (never lowered); the trailer has <c>/Size</c>, <c>/Root</c>,
/// <c>/Info</c> and <c>/ID</c> only.</item>
/// </list>
/// The output is written to a temporary file next to <c>outputPdf</c> and moved into place when complete.
/// </summary>
public static class PdfRewriter
{
    /// <summary>
    /// Writes <paramref name="outputPdf"/>: <paramref name="inputPdf"/> with exactly the objects in
    /// <paramref name="replacements"/> given new dictionaries and data (each dictionary is PDF syntax, as
    /// <see cref="PdfImageEncoder"/> writes it; its <c>/Length</c> is replaced by the data length). An existing file at
    /// <paramref name="outputPdf"/> is replaced only on success; on any exception nothing new is left there. Throws:
    /// <list type="bullet">
    /// <item><see cref="ArgumentException"/>: the caller's fault (output = input, an object the input does not have, a
    /// replacement dictionary that is not exactly one dictionary or cannot be written exactly);</item>
    /// <item><see cref="InvalidDataException"/>: the input cannot be parsed, an object cannot be read, or an object's
    /// bytes are not where its cross-reference entry and length say;</item>
    /// <item><see cref="NotSupportedException"/>: an encrypted input (<c>encrypted PDFs are not supported</c>), or an
    /// object from an object stream (or the trailer) that cannot be written back exactly;</item>
    /// <item><see cref="IOException"/>: reading or writing the files.</item>
    /// </list>
    /// If the temporary file cannot be deleted after a failure, the original exception is thrown with the path and
    /// reason in its <see cref="Exception.Data"/> under <see cref="TemporaryFileNotDeletedKey"/>.
    /// </summary>
    public static void Write(string inputPdf, string outputPdf, IReadOnlyDictionary<PdfObjectId, PdfImageStream> replacements)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPdf);
        ArgumentException.ThrowIfNullOrEmpty(outputPdf);
        ArgumentNullException.ThrowIfNull(replacements);
        if (string.Equals(Path.GetFullPath(inputPdf), Path.GetFullPath(outputPdf), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output PDF must not be the input PDF.", nameof(outputPdf));

        var streams = replacements.ToDictionary(
            r => new IndirectReference(r.Key.Number, r.Key.Generation),
            r => Prepare(r.Key, r.Value));

        // The ".tmp" extension keeps folder watchers away from it.
        var temp = $"{outputPdf}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                WriteAll(inputPdf, output, streams);
            File.Move(temp, outputPdf, overwrite: true);
        }
        catch (Exception ex)
        {
            if (DeleteFailed(temp) is { } reason) ex.Data[TemporaryFileNotDeletedKey] = $"{temp}: {reason}";
            throw;
        }
    }

    /// <summary>The key in <see cref="Exception.Data"/> under which a temporary file left behind is named.</summary>
    public const string TemporaryFileNotDeletedKey = "Illux.PdfRewriter.TemporaryFileNotDeleted";

    /// <summary>A replacement: its dictionary already written (with a direct <c>/Length</c>), and its data.</summary>
    private sealed record Replacement(byte[] Dictionary, byte[] Data);

    private static Replacement Prepare(PdfObjectId id, PdfImageStream? replacement)
    {
        var name = $"the replacement for object {id.Number} {id.Generation} R";
        if (replacement?.Dictionary is null || replacement.Data is null)
            throw new ArgumentException($"There is no dictionary or data for {name}.", "replacements");
        DictionaryToken dictionary;
        try
        {
            var scanner = new CoreTokenScanner(new MemoryInputBytes(replacement.Dictionary), true, new StackDepthGuard(64));
            dictionary = scanner.MoveNext() && scanner.CurrentToken is DictionaryToken d && !scanner.MoveNext()
                ? d
                : throw new ArgumentException($"The dictionary of {name} is not exactly one PDF dictionary.", "replacements");
        }
        catch (Exception ex) when (ex is not ArgumentException and not OutOfMemoryException)
        {
            throw new ArgumentException($"The dictionary of {name} is not exactly one PDF dictionary.", "replacements", ex);
        }
        try
        {
            return new Replacement(PdfSyntax.ToBytes(dictionary.With("Length", new NumericToken(replacement.Data.Length))), replacement.Data);
        }
        catch (NotSupportedException ex)
        {
            throw new ArgumentException($"The dictionary of {name} cannot be written exactly ({ex.Message}).", "replacements", ex);
        }
    }

    private static void WriteAll(string inputPdf, Stream output, Dictionary<IndirectReference, Replacement> streams)
    {
        using var input = File.OpenRead(inputPdf);
        var version = PdfImageScanner.HeaderVersion(input) ?? throw new InvalidDataException("the file has no %PDF header");
        input.Position = 0;
        using var document = Open(input);
        if (document.IsEncrypted) throw new NotSupportedException("encrypted PDFs are not supported");
        var structure = document.Structure;
        var locations = structure.CrossReferenceTable.ObjectOffsets;

        // One generation per number: the newest, if a damaged table lists several.
        var references = locations
            .Where(l => l.Value.Type != XrefEntryType.Free && l.Key.ObjectNumber > 0)
            .GroupBy(l => l.Key.ObjectNumber)
            .Select(g => g.MaxBy(l => l.Key.Generation).Key)
            .OrderBy(r => r.ObjectNumber)
            .ToList();
        foreach (var reference in streams.Keys.Where(r => !references.Contains(r)))
            throw new ArgumentException($"The PDF has no object {reference.ObjectNumber} {reference.Generation} R to replace.");

        using var file = new FileStream(inputPdf, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        var raw = new PdfRawObjects(file);
        new PdfObjectWriter(output).WriteHeader(version);
        var written = new SortedDictionary<long, (long Offset, int Generation)>();
        var dropped = new Dictionary<long, int>();
        foreach (var reference in references)
        {
            var at = output.Position;
            if (streams.TryGetValue(reference, out var replacement))
            {
                WriteStream(output, reference, replacement.Dictionary, replacement.Data);
            }
            else
            {
                var token = Read(structure, reference);
                if (IsLeftOut(token))
                {
                    dropped[reference.ObjectNumber] = reference.Generation;
                    continue;
                }
                var location = locations[reference];
                if (location.Type == XrefEntryType.File)
                {
                    var length = token is StreamToken stream ? CheckedLength(structure, reference, stream) : (long?)null;
                    var (start, end) = raw.Locate(reference, location.Value1, length);
                    raw.CopyTo(output, start, end);
                    Ascii(output, "\n");
                }
                else
                {
                    WriteSerialised(output, reference, token);
                }
            }
            written[reference.ObjectNumber] = (at, reference.Generation);
        }
        WriteTrailer(output, written, dropped, structure.Trailer);
    }

    private static PdfDocument Open(Stream input)
    {
        try
        {
            return PdfDocument.Open(input, new ParsingOptions { SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new NotSupportedException("encrypted PDFs are not supported");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"the file could not be parsed ({PdfImageScanner.FirstLine(ex.Message)})", ex);
        }
    }

    /// <summary>
    /// The object as PdfPig reads it. One it cannot read (even one nothing refers to) fails the rewrite: it cannot be
    /// located safely, and leaving it out could change what another reader shows.
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

    /// <summary>The stream's declared <c>/Length</c> (direct or indirect), which must be the length of the data PdfPig read.</summary>
    private static long CheckedLength(Structure structure, IndirectReference reference, StreamToken stream)
    {
        var token = stream.StreamDictionary.Data.GetValueOrDefault("Length");
        if (token is IndirectReferenceToken indirect) token = Read(structure, indirect.Data);
        if (token is not NumericToken { HasDecimalPlaces: false } length || length.Long != stream.Data.Length)
            throw new InvalidDataException(
                $"object {reference.ObjectNumber} {reference.Generation} R: its /Length does not match its {stream.Data.Length} bytes of data");
        return length.Long;
    }

    /// <summary>Object and cross-reference streams (replaced by the classic table) and a linearization dictionary.</summary>
    private static bool IsLeftOut(IToken token) => token switch
    {
        StreamToken stream => stream.StreamDictionary.Data.TryGetValue("Type", out var type) && type is NameToken { Data: "ObjStm" or "XRef" },
        DictionaryToken dictionary => dictionary.Data.ContainsKey("Linearized"),
        _ => false,
    };

    /// <summary>An object from an object stream, written again; a stream gets a direct <c>/Length</c>.</summary>
    private static void WriteSerialised(Stream output, IndirectReference reference, IToken token)
    {
        byte[] body;
        byte[]? data = null;
        try
        {
            if (token is StreamToken stream)
            {
                data = stream.Data.ToArray();
                body = PdfSyntax.ToBytes(stream.StreamDictionary.With("Length", new NumericToken(data.Length)));
            }
            else
            {
                body = PdfSyntax.ToBytes(token);
            }
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException(
                $"object {reference.ObjectNumber} {reference.Generation} R cannot be written back exactly ({ex.Message})", ex);
        }
        if (data != null)
        {
            WriteStream(output, reference, body, data);
            return;
        }
        Header(output, reference);
        output.Write(body);
        Ascii(output, "\nendobj\n");
    }

    private static void WriteStream(Stream output, IndirectReference reference, byte[] dictionary, byte[] data)
    {
        Header(output, reference);
        output.Write(dictionary);
        Ascii(output, "\nstream\n");
        output.Write(data);
        Ascii(output, "\nendstream\nendobj\n");
    }

    private static void Header(Stream output, IndirectReference reference) =>
        Ascii(output, string.Create(CultureInfo.InvariantCulture, $"{reference.ObjectNumber} {reference.Generation} obj\n"));

    /// <summary>
    /// One table from object 0 to the highest number written or dropped, 20-byte entries. Every number not written is free: a
    /// dropped object's number for its next generation, any other for generation 0; entry 0 starts the list that links
    /// them in ascending order and ends in 0.
    /// </summary>
    private static void WriteTrailer(Stream output, SortedDictionary<long, (long Offset, int Generation)> written,
        Dictionary<long, int> dropped, TrailerDictionary trailer)
    {
        byte[] info, id;
        try
        {
            info = trailer.Info is { } i ? PdfSyntax.ToBytes(i) : [];
            id = trailer.Identifier is { Count: > 0 } identifier ? PdfSyntax.ToBytes(new ArrayToken(identifier.Cast<IToken>().ToList())) : [];
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException($"the trailer cannot be written back exactly ({ex.Message})", ex);
        }

        var size = checked((int)written.Keys.Concat(dropped.Keys).DefaultIfEmpty(0).Max() + 1);
        var free = Enumerable.Range(1, size - 1).Where(n => !written.ContainsKey(n)).ToList();
        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {size}\n");
        table.Append(CultureInfo.InvariantCulture, $"{(free.Count > 0 ? free[0] : 0):D10} 65535 f\r\n");
        var freeIndex = 0;
        for (var number = 1; number < size; number++)
        {
            if (written.TryGetValue(number, out var entry))
            {
                table.Append(CultureInfo.InvariantCulture, $"{entry.Offset:D10} {entry.Generation:D5} n\r\n");
                continue;
            }
            var next = ++freeIndex < free.Count ? free[freeIndex] : 0;
            var generation = dropped.TryGetValue(number, out var old) ? Math.Min(old + 1, 65535) : 0;
            table.Append(CultureInfo.InvariantCulture, $"{next:D10} {generation:D5} f\r\n");
        }
        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {size} /Root {trailer.Root.ObjectNumber} {trailer.Root.Generation} R");
        Ascii(output, table.ToString());
        if (info.Length > 0)
        {
            Ascii(output, " /Info ");
            output.Write(info);
        }
        if (id.Length > 0)
        {
            Ascii(output, " /ID ");
            output.Write(id);
        }
        Ascii(output, string.Create(CultureInfo.InvariantCulture, $" >>\nstartxref\n{xref}\n%%EOF\n"));
    }

    private static void Ascii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));

    /// <summary>Deletes the file if it is there; the reason when that fails, else null.</summary>
    private static string? DeleteFailed(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
