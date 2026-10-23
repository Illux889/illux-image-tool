using System.Buffers.Binary;

namespace Illux.ImageTool.Pdf;

/// <summary>Reads the EXIF Orientation tag (1-8) of a JPEG from its APP1 segment, without decoding the image.</summary>
internal static class ExifOrientation
{
    private const ushort OrientationTag = 0x0112;
    private const ushort TypeShort = 3;

    /// <summary>The orientation 1-8; 1 when the file has none or it cannot be read.</summary>
    public static int ReadJpeg(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> marker = stackalloc byte[4];
        if (input.ReadAtLeast(marker[..2], 2, throwOnEndOfStream: false) < 2 || marker[0] != 0xFF || marker[1] != 0xD8) return 1;

        while (input.ReadAtLeast(marker, 4, throwOnEndOfStream: false) == 4 && marker[0] == 0xFF)
        {
            var type = marker[1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(marker[2..]) - 2;
            if (type is 0xDA or 0xD9 || length < 0) return 1; // image data reached: no EXIF before it
            if (type == 0xE1 && length >= 14)
            {
                var segment = new byte[length];
                if (input.ReadAtLeast(segment, length, throwOnEndOfStream: false) < length) return 1;
                if (segment.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)) return FromTiff(segment.AsSpan(6));
                continue;
            }
            input.Seek(length, SeekOrigin.Current);
        }
        return 1;
    }

    /// <summary>The Orientation entry of IFD0 in a TIFF structure (the EXIF payload).</summary>
    internal static int FromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return 1;

        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifd = U32(tiff[4..]);
        if (ifd > (uint)tiff.Length - 2) return 1;
        var count = U16(tiff[(int)ifd..]);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) return 1;
            if (U16(tiff[entry..]) != OrientationTag) continue;
            if (U16(tiff[(entry + 2)..]) != TypeShort) return 1;
            var value = U16(tiff[(entry + 8)..]);
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }
}
