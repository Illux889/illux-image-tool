using System.Text;
using UglyToad.PdfPig.Core;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Finds and copies the raw bytes of an object stored plainly in a PDF file: from <c>n g obj</c> through
/// <c>endobj</c> (ruling P1b). The end is found by reading the syntax, not by searching for the keyword: strings,
/// hex strings, comments and names are stepped over, and a stream's data is skipped by its length. Whatever is not
/// where it should be fails with <see cref="InvalidDataException"/> rather than being guessed.
/// </summary>
internal sealed class PdfRawObjects(Stream file)
{
    private const string Delimiters = "()<>[]{}/%";
    private readonly byte[] _window = new byte[64 * 1024];
    private long _windowStart = -1;
    private int _windowLength;

    /// <summary>
    /// The byte range [Start, End) of the object at <paramref name="offset"/>, which must start with
    /// <c>n g obj</c> for <paramref name="reference"/>. A stream (<paramref name="streamLength"/> not null) must have
    /// <c>stream</c>, an end-of-line, exactly that many bytes, then <c>endstream</c> and <c>endobj</c> (whitespace
    /// between them); anything else must reach <c>endobj</c> without a <c>stream</c> keyword.
    /// </summary>
    public (long Start, long End) Locate(IndirectReference reference, long offset, long? streamLength)
    {
        void Fail(string what) => throw new InvalidDataException(
            $"object {reference.ObjectNumber} {reference.Generation} R: {what} in the file (offset {offset})");

        var p = SkipWhitespace(offset);
        var start = p;
        if (!TryInteger(ref p, reference.ObjectNumber) || !TrySkipWhitespace(ref p) ||
            !TryInteger(ref p, reference.Generation) || !Keyword(SkipWhitespace(p), "obj"))
            Fail("its \"n g obj\" is not at its cross-reference offset");
        p = SkipWhitespace(p) + 3;

        while (true)
        {
            var c = At(p);
            if (c < 0) Fail("the end of the file comes before endobj");
            if (c == '%')
            {
                while (At(p) is >= 0 and not '\r' and not '\n') p++;
            }
            else if (c == '(')
            {
                p = SkipLiteral(p);
                if (p < 0) Fail("a string does not end");
            }
            else if (c == '<' && At(p + 1) != '<')
            {
                while (At(p) is >= 0 and not '>') p++;
                if (At(p) < 0) Fail("a hex string does not end");
                p++;
            }
            else if (IsRegular(c) && At(p - 1) != '/')
            {
                if (Keyword(p, "endobj"))
                {
                    if (streamLength != null) Fail("a stream has no stream keyword");
                    return (start, p + 6);
                }
                if (Keyword(p, "stream"))
                {
                    if (streamLength is not { } length) Fail("an object that is not a stream has a stream keyword");
                    else return (start, EndOfStream(p + 6, length, Fail));
                }
                while (IsRegular(At(p))) p++;
            }
            else
            {
                p++;
            }
        }
    }

    /// <summary>Copies [<paramref name="start"/>, <paramref name="end"/>) of the file to <paramref name="output"/>.</summary>
    public void CopyTo(Stream output, long start, long end)
    {
        file.Position = start;
        var buffer = new byte[1 << 20];
        for (var left = end - start; left > 0;)
        {
            var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (read <= 0) throw new EndOfStreamException("The PDF file ended while an object was copied.");
            output.Write(buffer, 0, read);
            left -= read;
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

    private bool TryInteger(ref long p, long expected)
    {
        var digits = new StringBuilder();
        while (At(p) is >= '0' and <= '9') digits.Append((char)At(p++));
        return digits.Length > 0 && long.TryParse(digits.ToString(), out var value) && value == expected;
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
