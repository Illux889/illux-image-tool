using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Builds raw JPEG metadata segments and splices them into a file. ImageMagick re-syncs EXIF and Photoshop
/// resolution with its own density when it writes, so files with conflicting resolution tags (as produced by
/// other software) have to be assembled at byte level.
/// </summary>
internal static class JpegSegments
{
    /// <summary>APP1 Exif segment with X/YResolution and ResolutionUnit (2 = inch, 3 = cm).</summary>
    public static byte[] Exif(double resolution, ushort unit = 2)
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.XResolution, new Rational(resolution));
        exif.SetValue(ExifTag.YResolution, new Rational(resolution));
        exif.SetValue(ExifTag.ResolutionUnit, unit);
        var data = exif.ToByteArray()!;
        var header = "Exif\0\0"u8.ToArray();
        if (!data.AsSpan().StartsWith(header)) data = [.. header, .. data];
        return Segment(0xE1, data);
    }

    /// <summary>APP13 "Photoshop 3.0" segment holding one 8BIM ResolutionInfo (0x03ED) resource.</summary>
    public static byte[] PhotoshopResolution(double ppi) =>
        Segment(0xED, [.. Encoding.ASCII.GetBytes("Photoshop 3.0\0"), .. ResolutionInfo(ppi)]);

    /// <summary>The raw 8BIM resource block: signature, ID 0x03ED, empty name, size 16, ResolutionInfo.</summary>
    public static byte[] ResolutionInfo(double ppi)
    {
        var block = new byte[12 + 16];
        "8BIM"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(4), 0x03ED);
        // bytes 6..7: empty Pascal name padded to even length
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), 16);
        var fixedPpi = (uint)Math.Round(ppi * 65536);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(12), fixedPpi); // hRes (16.16 fixed, ppi)
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(16), 1);        // hResUnit: ppi
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(18), 1);        // widthUnit: inches
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(20), fixedPpi); // vRes
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(26), 1);
        return block;
    }

    /// <summary>Inserts the segments directly after the SOI marker and the JFIF APP0 segment (if any).</summary>
    public static void Insert(string path, params byte[][] segments)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 2;
        if (bytes[2] == 0xFF && bytes[3] == 0xE0) pos = 4 + ((bytes[4] << 8) | bytes[5]);
        File.WriteAllBytes(path, [.. bytes[..pos], .. segments.SelectMany(s => s), .. bytes[pos..]]);
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }
}
