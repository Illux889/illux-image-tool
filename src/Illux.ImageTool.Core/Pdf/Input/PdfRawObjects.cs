using System.Text;
using UglyToad.PdfPig.Core;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Finds and copies the raw bytes of an object stored plainly in a PDF file: from <c>n g obj</c> through
/// <c>endobj</c> (ruling P1b). The end is found by reading the syntax token by token, not by searching for the
/// keyword: a literal string is skipped to its matching <c>)</c> (nesting and <c>\</c> escapes followed), a hex string
/// to its <c>&gt;</c>, a name from <c>/</c> to its last regular byte, a comment to the end of its line; <c>&lt;&lt;</c>
/// and <c>&gt;&gt;</c> are single delimiters; <c>stream</c> and <c>endobj</c> count only as whole tokens; a stream's
/// data is skipped by its length. The result is then checked against <paramref name="boundaries"/> (every offset a
/// cross-reference section of the file names: objects of all revisions and the sections themselves): no boundary may
/// lie inside the object, and after its <c>endobj</c> only whitespace and comments may come before the next boundary,
/// an <c>n g obj</c>, <c>xref</c>, <c>trailer</c>, <c>startxref</c> or the end of the file. Whatever is not where it
/// should be fails with <see cref="InvalidDataException"/> rather than being guessed.
/// </summary>
internal sealed class PdfRawObjects(Stream file, IEnumerable<long> boundaries)
{
    private const string Delimiters = "()<>[]{}/%";
    private readonly long[] _boundaries = boundaries.Append(file.Length).Distinct().Order().ToArray();
    private readonly byte[] _window = new byte[64 * 1024];
    private long _windowStart = -1;
    private int _windowLength;

    /// <summary>
    /// The byte range [Start, End) of the object at <paramref name="offset"/>, which must start with
    /// <c>n g obj</c> for <paramref name="reference"/> (after a delimiter, whitespace or the file start). A stream
    /// (<paramref name="streamLength"/> not null) must have <c>stream</c>, an end-of-line, exactly that many bytes,
    /// then <c>endstream</c> and <c>endobj</c> (whitespace between them); anything else must reach <c>endobj</c>
    /// without a <c>stream</c> keyword.
    /// </summary>
    public (long Start, long End) Locate(IndirectReference reference, long offset, long? streamLength)
    {
        void Fail(string what) => throw new InvalidDataException(
            $"object {reference.ObjectNumber} {reference.Generation} R: {what} in the file (offset {offset})");

        var p = SkipWhitespace(offset);
        var start = p;
        if (!IsTokenStart(p) || !TryInteger(ref p, reference.ObjectNumber) || !TrySkipWhitespace(ref p) ||
            !TryInteger(ref p, reference.Generation) || !Keyword(SkipWhitespace(p), "obj"))
            Fail("its \"n g obj\" is not at its cross-reference offset");
        p = SkipWhitespace(p) + 3;

        var end = -1L;
        while (end < 0)
        {
            var c = At(p);
            if (c < 0) Fail("the end of the file comes before endobj");
            else if (c == '%') p = SkipComment(p);
            else if (c == '(')
            {
                p = SkipLiteral(p);
                if (p < 0) Fail("a string does not end");
            }
            else if (c == '<' && At(p + 1) == '<') p += 2;
            else if (c == '>' && At(p + 1) == '>') p += 2;
            else if (c == '<')
            {
                while (At(p) is >= 0 and not '>') p++;
                if (At(p) < 0) Fail("a hex string does not end");
                p++;
            }
            else if (c == '/')
            {
                p++;
                while (IsRegular(At(p))) p++;
            }
            else if (IsRegular(c))
            {
                if (Keyword(p, "endobj"))
                {
                    if (streamLength != null) Fail("a stream has no stream keyword");
                    end = p + 6;
                }
                else if (Keyword(p, "stream"))
                {
                    if (streamLength is not { } length) Fail("an object that is not a stream has a stream keyword");
                    else end = EndOfStream(p + 6, length, Fail);
                }
                else
                {
                    while (IsRegular(At(p))) p++;
                }
            }
            else p++;
        }

        // A boundary inside the object means it ran into another one; anything but whitespace or comments after it
        // means it was cut short.
        var next = NextBoundary(start);
        if (end > next) Fail($"it runs past offset {next}, where the cross-reference data puts something else");
        var after = end;
        while (true)
        {
            after = SkipWhitespace(after);
            if (At(after) != '%') break;
            after = SkipComment(after);
        }
        if (after < next && At(after) >= 0 && !IsObjectHeader(after) &&
            !Keyword(after, "xref") && !Keyword(after, "trailer") && !Keyword(after, "startxref"))
            Fail("something other than whitespace or a comment follows its endobj");
        return (start, end);
    }

    /// <summary>Copies [<paramref name="start"/>, <paramref name="end"/>) of the file to <paramref name="output"/>.</summary>
    public void CopyTo(Stream output, long start, long end)
    {
        file.Position = start;
        var buffer = new byte[1 << 20];
        for (var left = end - start; left > 0;)
        {
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) throw new InvalidDataException("the PDF file ended while an object was copied");
            output.Write(buffer, 0, read);
            left -= read;
        }
    }

    /// <summary>
    /// The entries of the classic cross-reference section at <paramref name="offset"/>: object number, generation and
    /// whether it is in use (<c>n</c>) or free (<c>f</c>).
    /// </summary>
    public IEnumerable<(long Number, int Generation, bool InUse)> ReadClassicSection(long offset)
    {
        void Fail(string what) => throw new InvalidDataException($"the cross-reference table at offset {offset}: {what}");

        var p = SkipWhitespace(offset);
        if (!Keyword(p, "xref")) Fail("it does not start with xref");
        p += 4;
        var entries = new List<(long, int, bool)>();
        while (true)
        {
            p = SkipWhitespace(p);
            if (Keyword(p, "trailer")) return entries;
            if (!TryReadInteger(ref p, out var first) || !TrySkipWhitespace(ref p) || !TryReadInteger(ref p, out var count))
                Fail("a subsection header is not two numbers");
            for (var i = 0L; i < count; i++)
            {
                p = SkipWhitespace(p);
                if (!TryReadInteger(ref p, out _) || !TrySkipWhitespace(ref p) || !TryReadInteger(ref p, out var generation) ||
                    !TrySkipWhitespace(ref p) || At(p) is not ('n' or 'f'))
                    Fail($"entry {first + i} is not \"offset generation n|f\"");
                entries.Add((first + i, (int)generation, At(p) == 'n'));
                p++;
            }
        }
    }

    /// <summary>After <c>stream</c>: CR LF, LF or CR, the data, whitespace, <c>endstream</c>, whitespace, <c>endobj</c>.</summary>
    private long EndOfStream(long p, long length, Action<string> fail)
    {
        if (At(p) == '\r') p += At(p + 1) == '\n' ? 2 : 1;
        else if (At(p) == '\n') p++;
        else fail("the stream keyword is not followed by an end-of-line");
        p = SkipWhitespace(p + length);
        if (!Keyword(p, "endstream")) fail($"endstream is not after the stream's {length} bytes");
        p = SkipWhitespace(p + 9);
        if (!Keyword(p, "endobj")) fail("endobj does not follow endstream");
        return p + 6;
    }

    /// <summary>The first boundary after <paramref name="position"/>.</summary>
    private long NextBoundary(long position)
    {
        var index = Array.BinarySearch(_boundaries, position + 1);
        if (index < 0) index = ~index;
        return index < _boundaries.Length ? _boundaries[index] : long.MaxValue;
    }

    /// <summary><c>n g obj</c> at <paramref name="p"/>, for any numbers.</summary>
    private bool IsObjectHeader(long p) =>
        TryReadInteger(ref p, out _) && TrySkipWhitespace(ref p) && TryReadInteger(ref p, out _) && Keyword(SkipWhitespace(p), "obj");

    /// <summary>A literal string from its "(" to after its ")", nested parentheses and escapes followed; -1 if it does not end.</summary>
    private long SkipLiteral(long p)
    {
        var depth = 0;
        while (true)
        {
            switch (At(p++))
            {
                case < 0: return -1;
                case '\\': p++; break;
                case '(': depth++; break;
                case ')' when --depth == 0: return p;
            }
        }
    }

    private long SkipComment(long p)
    {
        while (At(p) is >= 0 and not '\r' and not '\n') p++;
        return p;
    }

    private bool TryInteger(ref long p, long expected) => TryReadInteger(ref p, out var value) && value == expected;

    private bool TryReadInteger(ref long p, out long value)
    {
        var digits = new StringBuilder();
        while (At(p) is >= '0' and <= '9') digits.Append((char)At(p++));
        value = 0;
        return digits.Length > 0 && long.TryParse(digits.ToString(), out value);
    }

    private bool TrySkipWhitespace(ref long p)
    {
        var before = p;
        p = SkipWhitespace(p);
        return p > before;
    }

    private long SkipWhitespace(long p)
    {
        while (IsWhitespace(At(p))) p++;
        return p;
    }

    /// <summary>The start of the file, or a byte after whitespace or a delimiter.</summary>
    private bool IsTokenStart(long p) => p == 0 || !IsRegular(At(p - 1));

    /// <summary>The keyword at <paramref name="p"/>, followed by whitespace, a delimiter or the end of the file.</summary>
    private bool Keyword(long p, string keyword)
    {
        for (var i = 0; i < keyword.Length; i++)
            if (At(p + i) != keyword[i]) return false;
        return !IsRegular(At(p + keyword.Length));
    }

    private static bool IsWhitespace(int c) => c is 0 or '\t' or '\n' or '\f' or '\r' or ' ';

    private static bool IsRegular(int c) => c >= 0 && !IsWhitespace(c) && !Delimiters.Contains((char)c, StringComparison.Ordinal);

    /// <summary>The byte at <paramref name="position"/>, or -1 outside the file.</summary>
    private int At(long position)
    {
        if (position < 0) return -1;
        if (position < _windowStart || position >= _windowStart + _windowLength)
        {
            _windowStart = Math.Max(0, position - 1024);
            file.Position = _windowStart;
            _windowLength = file.ReadAtLeast(_window, _window.Length, throwOnEndOfStream: false);
            if (position >= _windowStart + _windowLength) return -1;
        }
        return _window[position - _windowStart];
    }
}
