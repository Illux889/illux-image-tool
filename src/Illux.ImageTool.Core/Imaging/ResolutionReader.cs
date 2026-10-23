using System.Buffers.Binary;
using ImageMagick;

namespace Illux.ImageTool.Imaging;

/// <summary>Image resolution in pixels per inch.</summary>
public sealed record Resolution(double X, double Y);

/// <summary>
/// Reads the stored resolution the way Photoshop does when a file carries several resolution tags.
/// </summary>
/// <remarks>
/// Order (first valid source wins):
/// <list type="number">
/// <item>Photoshop image resource ResolutionInfo (8BIM 0x03ED, in JPEG APP13 / TIFF tag 34377).</item>
/// <item>EXIF X/YResolution with ResolutionUnit.</item>
/// <item>The density in the file's own header (JFIF APP0, TIFF IFD0 X/YResolution, PNG pHYs, BMP).</item>
/// </list>
/// The brief proposed header density before EXIF. Adobe publishes no precedence rule, but observations of
/// Photoshop agree that for JPEG it is "RID_RESOLUTION overwrites EXIF-Resolution overwrites JPEG-Resolution"
/// (ImageMagick forum, "Changing density does not work completely",
/// https://jqmagick.imagemagick.org/discourse-server/viewtopic.php?p=32454), and ExifTool's author notes that
/// Photoshop ignores the EXIF resolution when a Photoshop resolution (0x03ED) is present
/// (ExifTool forum, "PhotoShop Resolution", https://exiftool.org/forum/index.php?msg=6623).
/// So EXIF is read before the JFIF density. For TIFF, EXIF X/YResolution and the header density are the same
/// IFD0 tags, so the order only matters for JPEG.
/// <para>
/// ResolutionInfo layout (Adobe Photoshop File Formats Specification, image resource 0x03ED; ExifTool
/// "Photoshop Tags"): hRes Fixed 16.16, hResUnit int16, widthUnit int16, vRes Fixed 16.16, vResUnit int16,
/// heightUnit int16. hRes/vRes are always pixels per inch; the unit fields only choose how Photoshop displays them.
/// </para>
/// </remarks>
public static class ResolutionReader
{
    private const ushort ResolutionInfoId = 0x03ED;
    private static readonly byte[] PhotoshopHeader = "Photoshop 3.0\0"u8.ToArray();
    private static readonly string[] ResourceSignatures = ["8BIM", "MeSa", "PHUT", "AgHg", "DCSR"];

    /// <summary>Returns the resolution in ppi, or null when no source stores a usable resolution.</summary>
    public static Resolution? Read(IMagickImage<float> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return From8Bim(image.GetProfile("8bim")?.ToByteArray())
            ?? FromExif(image.GetExifProfile())
            ?? FromDensity(image.Density);
    }

    /// <summary>Finds ResolutionInfo (0x03ED) in a Photoshop image resource block; null if absent or malformed.</summary>
    internal static Resolution? From8Bim(byte[]? data)
    {
        if (data is null) return null;
        var span = data.AsSpan();
        if (span.StartsWith(PhotoshopHeader)) span = span[PhotoshopHeader.Length..];

        var pos = 0;
        while (pos + 12 <= span.Length)
        {
            var signature = System.Text.Encoding.ASCII.GetString(span.Slice(pos, 4));
            if (!ResourceSignatures.Contains(signature)) return null;
            var id = BinaryPrimitives.ReadUInt16BigEndian(span[(pos + 4)..]);
            pos += 6;

            var nameLength = 1 + span[pos];      // Pascal string: length byte + characters,
            pos += nameLength + (nameLength & 1); // padded to an even size
            if (pos + 4 > span.Length) return null;
            var size = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]);
            pos += 4;
            if (size > (uint)(span.Length - pos)) return null;

            if (id == ResolutionInfoId)
            {
                if (size < 16) return null;
                var x = BinaryPrimitives.ReadUInt32BigEndian(span[pos..]) / 65536.0;
                var y = BinaryPrimitives.ReadUInt32BigEndian(span[(pos + 8)..]) / 65536.0;
                return Valid(x, y);
            }

            pos += (int)size + (int)(size & 1);
        }
        return null;
    }

    private static Resolution? FromExif(IExifProfile? exif)
    {
        if (exif is null) return null;
        var x = exif.GetValue(ExifTag.XResolution)?.Value.ToDouble();
        var y = exif.GetValue(ExifTag.YResolution)?.Value.ToDouble();
        if (x is null || y is null) return null;

        // ResolutionUnit: 1 = no absolute unit, 2 = inch (the EXIF default when the tag is missing), 3 = centimetre.
        var unit = exif.GetValue(ExifTag.ResolutionUnit)?.Value ?? 2;
        return unit switch
        {
            2 => Valid(x.Value, y.Value),
            3 => Valid(x.Value * 2.54, y.Value * 2.54),
            _ => null,
        };
    }

    private static Resolution? FromDensity(Density density) => density.Units switch
    {
        DensityUnit.PixelsPerInch => Valid(density.X, density.Y),
        DensityUnit.PixelsPerCentimeter => Valid(density.X * 2.54, density.Y * 2.54),
        _ => null, // JFIF units 0 is an aspect ratio, not a resolution
    };

    private static Resolution? Valid(double x, double y) =>
        x > 0 && y > 0 && double.IsFinite(x) && double.IsFinite(y) ? new Resolution(x, y) : null;
}
