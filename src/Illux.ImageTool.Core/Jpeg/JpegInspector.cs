using System.Text;

namespace Illux.ImageTool.Jpeg;

public enum JpegCoding { Baseline, ExtendedSequential, Progressive, Lossless, Arithmetic }

/// <param name="AdobeTransform">Adobe APP14 transform byte, or -1 when there is no Adobe marker.</param>
/// <param name="JfifDensity">JFIF density as stored; <c>Units</c> is 0 none, 1 dpi, 2 dpcm. Null without a JFIF APP0 marker.</param>
/// <param name="IccProfile">ICC profile reassembled from all APP2 chunks, or null.</param>
public sealed record JpegInfo(
    JpegCoding Coding,
    int Precision,
    int Width,
    int Height,
    int Components,
    bool HasAdobeMarker,
    int AdobeTransform,
    (double X, double Y, int Units)? JfifDensity,
    byte[]? IccProfile);

/// <summary>Reads JPEG header segments (up to the first SOS) without decoding the image.</summary>
public static class JpegInspector
{
    private static readonly byte[] IccSignature = Encoding.ASCII.GetBytes("ICC_PROFILE\0");
    private static readonly byte[] JfifSignature = Encoding.ASCII.GetBytes("JFIF\0");
    private static readonly byte[] AdobeSignature = Encoding.ASCII.GetBytes("Adobe");

    public static JpegInfo Inspect(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Inspect(fs);
    }

    public static JpegInfo Inspect(Stream s)
    {
        if (ReadByte(s) != 0xFF || ReadByte(s) != 0xD8)
            throw new InvalidDataException("Not a JPEG file (missing SOI marker).");

        JpegCoding? coding = null;
        int precision = 0, width = 0, height = 0, components = 0;
        var hasAdobe = false;
        var adobeTransform = -1;
        (double X, double Y, int Units)? density = null;
        var iccChunks = new SortedDictionary<int, byte[]>();

        while (true)
        {
            var marker = NextMarker(s);
            if (marker is 0xD9 or 0xDA) break; // EOI or SOS: header is complete
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) continue; // standalone markers carry no length

            var length = (ReadByte(s) << 8) | ReadByte(s);
            if (length < 2) throw new InvalidDataException("Invalid JPEG segment length.");
            var data = ReadExactly(s, length - 2);

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (coding != null) continue; // first frame header wins
                if (data.Length < 6) throw new InvalidDataException("Truncated JPEG frame header.");
                coding = marker switch
                {
                    0xC0 => JpegCoding.Baseline,
                    0xC1 => JpegCoding.ExtendedSequential,
                    0xC2 => JpegCoding.Progressive,
                    0xC3 or 0xC7 or 0xCB or 0xCF => JpegCoding.Lossless,
                    0xC9 or 0xCA or 0xCD or 0xCE => JpegCoding.Arithmetic,
                    _ => throw new InvalidDataException($"Unsupported JPEG frame type SOF{marker - 0xC0} (hierarchical).")
                };
                precision = data[0];
                height = (data[1] << 8) | data[2];
                width = (data[3] << 8) | data[4];
                components = data[5];
            }
            else if (marker == 0xE0 && StartsWith(data, JfifSignature) && data.Length >= 12)
            {
                density = (
                    (data[8] << 8) | data[9],
                    (data[10] << 8) | data[11],
                    data[7]);
            }
            else if (marker == 0xE2 && StartsWith(data, IccSignature) && data.Length > IccSignature.Length + 2)
            {
                var seq = data[IccSignature.Length];
                iccChunks[seq] = data[(IccSignature.Length + 2)..];
            }
            else if (marker == 0xEE && StartsWith(data, AdobeSignature) && data.Length >= 12)
            {
                hasAdobe = true;
                adobeTransform = data[11];
            }
        }

        if (coding is null) throw new InvalidDataException("JPEG has no frame header.");

        byte[]? icc = null;
        if (iccChunks.Count > 0)
        {
            icc = new byte[iccChunks.Values.Sum(c => c.Length)];
            var offset = 0;
            foreach (var chunk in iccChunks.Values)
            {
                chunk.CopyTo(icc, offset);
                offset += chunk.Length;
            }
        }

        return new JpegInfo(coding.Value, precision, width, height, components, hasAdobe, adobeTransform, density, icc);
    }

    /// <summary>Skips to the next marker (0xFF followed by a non-0x00, non-0xFF byte) and returns its code.</summary>
    private static int NextMarker(Stream s)
    {
        var b = ReadByte(s);
        while (true)
        {
            while (b != 0xFF) b = ReadByte(s);
            while (b == 0xFF) b = ReadByte(s); // fill bytes
            if (b != 0x00) return b;
        }
    }

    private static int ReadByte(Stream s)
    {
        var b = s.ReadByte();
        if (b < 0) throw new InvalidDataException("Unexpected end of JPEG data.");
        return b;
    }

    private static byte[] ReadExactly(Stream s, int count)
    {
        var buffer = new byte[count];
        try { s.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw new InvalidDataException("Unexpected end of JPEG data."); }
        return buffer;
    }

    private static bool StartsWith(byte[] data, byte[] prefix) =>
        data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
