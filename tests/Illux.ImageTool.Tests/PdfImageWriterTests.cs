using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using ICCBasedColorSpaceDetails = UglyToad.PdfPig.Graphics.Colors.ICCBasedColorSpaceDetails;

namespace Illux.ImageTool.Tests;

public sealed class PdfImageWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string WritePdf(string imagePath, double ppiX = 200, double? ppiY = null)
    {
        var pdf = Path.ChangeExtension(imagePath, ".pdf");
        PdfImageWriter.Write(imagePath, new Resolution(ppiX, ppiY ?? ppiX), pdf);
        return pdf;
    }

    /// <summary>The PDF as Latin-1 text, so dictionary keys can be searched and binary data keeps one char per byte.</summary>
    private static string RawText(string pdf) => Encoding.Latin1.GetString(File.ReadAllBytes(pdf));

    private static IPdfImage SingleImage(string pdf, out Page page)
    {
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        page = document.GetPage(1);
        return Assert.Single(page.GetImages());
    }

    private static byte[] Inflate(ReadOnlySpan<byte> zlib)
    {
        using var input = new MemoryStream(zlib.ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The source pixels as PDF samples: 8-bit bytes, or 16-bit big-endian.</summary>
    private static byte[] SourceSamples(string path, string mapping, int bits)
    {
        using var image = new MagickImage(path);
        using var pixels = image.GetPixels();
        if (bits == 8) return pixels.ToByteArray(mapping)!;
        var shorts = pixels.ToShortArray(mapping)!;
        var bytes = new byte[shorts.Length * 2];
        for (var i = 0; i < shorts.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), shorts[i]);
        return bytes;
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    /// <summary>Removes the Adobe APP14 segment (and nothing else) from a JPEG.</summary>
    private static void RemoveApp14(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 2;
        while (bytes[pos] == 0xFF && bytes[pos + 1] != 0xDA)
        {
            var length = (bytes[pos + 2] << 8) | bytes[pos + 3];
            if (bytes[pos + 1] == 0xEE)
            {
                File.WriteAllBytes(path, [.. bytes[..pos], .. bytes[(pos + 2 + length)..]]);
                return;
            }
            pos += 2 + length;
        }
        throw new InvalidOperationException("No APP14 segment found.");
    }

    [Fact]
    public void Pdf_HeaderIs16()
    {
        var pdf = WritePdf(TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 200));

        var bytes = File.ReadAllBytes(pdf);
        Assert.Equal("%PDF-1.6\n"u8.ToArray(), bytes[..9]);
        // Second line: a comment with at least four bytes >= 128, so transfer tools treat the file as binary.
        Assert.Equal((byte)'%', bytes[9]);
        Assert.True(bytes[10..14].All(b => b >= 128));
    }

    [Fact]
    public void Pdf_PageSize_FromPpi()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "big.jpg");
        using (var image = new MagickImage(MagickColors.White, 4724, 3150)) image.Write(path);

        var pdf = WritePdf(path, 200);

        SingleImage(pdf, out var page);
        Assert.Equal(1700.64, page.Width, 0.01);
        Assert.Equal(1134, page.Height, 0.01);
        Assert.Equal(600, page.Width / 72 * 25.4, 0.1); // 599.95 mm: 4724 px is the rounded pixel size of 600 mm
        Assert.Equal(400, page.Height / 72 * 25.4, 0.1);
        var raw = RawText(pdf);
        Assert.Contains("/MediaBox [0 0 1700.64 1134]", raw);
        Assert.Contains("/TrimBox [0 0 1700.64 1134]", raw);
        Assert.Contains("q 1700.64 0 0 1134 0 0 cm /Im0 Do Q", raw);
    }

    [Fact]
    public void Pdf_PageSize_PerAxisPpi()
    {
        var pdf = WritePdf(TestImages.Write(_dir, "a.jpg", 300, 200, ColorSpace.sRGB, 8, 150), ppiX: 150, ppiY: 300);

        var image = SingleImage(pdf, out var page);
        Assert.Equal(144, page.Width, 0.01);
        Assert.Equal(48, page.Height, 0.01);
        Assert.Equal(144, image.BoundingBox.Width, 0.01);
        Assert.Equal(48, image.BoundingBox.Height, 0.01);
    }

    [Fact]
    public void Pdf_LargePage_UsesUserUnit()
    {
        // A 40,500 × 20,000 px image would take gigabytes to decode; JPEG embedding reads only the header, so a
        // small JPEG with the frame size patched in its SOF0 segment is enough.
        var path = TestImages.Write(_dir, "huge.jpg", 64, 48, ColorSpace.sRGB, 8, 200);
        var bytes = File.ReadAllBytes(path);
        var sof = bytes.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(sof + 5), 20000);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(sof + 7), 40500);
        File.WriteAllBytes(path, bytes);

        var pdf = WritePdf(path, 200);

        var raw = RawText(pdf);
        Assert.Contains("/UserUnit 2 /MediaBox [0 0 7290 3600] /TrimBox [0 0 7290 3600]", raw);
        Assert.Contains("q 7290 0 0 3600 0 0 cm /Im0 Do Q", raw);
        SingleImage(pdf, out var page);
        Assert.Equal(14580, page.Width * 2, 0.01); // physical width = box × UserUnit
        Assert.Equal(7200, page.Height * 2, 0.01);
    }

    [Fact]
    public void Pdf_NormalPage_NoUserUnit()
    {
        Assert.DoesNotContain("/UserUnit", RawText(WritePdf(TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 200))));
    }

    [Fact]
    public void Pdf_Jpeg_EmbeddedByteForByte()
    {
        var path = TestImages.Write(_dir, "a.jpg", 120, 80, ColorSpace.sRGB, 8, 200, icc: TestImages.SrgbIcc);

        var pdf = WritePdf(path);

        var jpeg = File.ReadAllBytes(path);
        Assert.True(ContainsSequence(File.ReadAllBytes(pdf), jpeg));
        Assert.Contains("/Filter /DCTDecode", RawText(pdf));
        var image = SingleImage(pdf, out _);
        Assert.Equal(jpeg, image.RawBytes.ToArray());
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal(120, image.WidthInSamples);
        Assert.Equal(80, image.HeightInSamples);
    }

    [Theory]
    [InlineData(ColorSpace.sRGB, 8, "RGB")]
    [InlineData(ColorSpace.sRGB, 16, "RGB")]
    [InlineData(ColorSpace.Gray, 8, "R")]
    [InlineData(ColorSpace.Gray, 16, "R")]
    [InlineData(ColorSpace.CMYK, 8, "CMYK")]
    public void Pdf_Tiff_IsFlateLossless(ColorSpace cs, int depth, string mapping)
    {
        var path = TestImages.Write(_dir, "a.tif", 97, 61, cs, depth, 200);

        var pdf = WritePdf(path);

        Assert.Contains("/Filter /FlateDecode", RawText(pdf));
        var image = SingleImage(pdf, out _);
        Assert.Equal(depth, image.BitsPerComponent);
        Assert.Equal(SourceSamples(path, mapping, depth), Inflate(image.RawBytes));
        if (depth == 8)
        {
            Assert.True(image.TryGetBytesAsMemory(out var decoded));
            Assert.Equal(SourceSamples(path, mapping, depth), decoded.ToArray());
        }
    }

    [Fact]
    public void Pdf_IccBased_WhenProfilePresent()
    {
        var pdf = WritePdf(TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 200, icc: TestImages.SrgbIcc));

        var raw = RawText(pdf);
        Assert.Matches(@"/ColorSpace \[/ICCBased \d+ 0 R\]", raw);
        Assert.Contains("/N 3 /Alternate /DeviceRGB", raw);
        Assert.True(ContainsSequence(File.ReadAllBytes(pdf), TestImages.SrgbIcc));
        var icc = Assert.IsType<ICCBasedColorSpaceDetails>(SingleImage(pdf, out _).ColorSpaceDetails);
        Assert.Equal(3, icc.NumberOfColorComponents);
    }

    [Fact]
    public void Pdf_IccBased_CmykTiff()
    {
        var pdf = WritePdf(TestImages.Write(_dir, "a.tif", 64, 48, ColorSpace.CMYK, 8, 200, icc: TestImages.CmykIcc));

        var raw = RawText(pdf);
        Assert.Matches(@"/ColorSpace \[/ICCBased \d+ 0 R\]", raw);
        Assert.Contains("/N 4 /Alternate /DeviceCMYK", raw);
        Assert.True(ContainsSequence(File.ReadAllBytes(pdf), TestImages.CmykIcc));
    }

    [Fact]
    public void Pdf_IccBased_PngWithIccp()
    {
        var path = TestImages.Write(_dir, "a.png", 64, 48, ColorSpace.sRGB, 8, 200, alpha: true);
        using (var image = new MagickImage(path))
        {
            image.SetProfile(new ColorProfile(TestImages.SrgbIcc));
            image.Settings.SetDefine(MagickFormat.Png, "preserve-iCCP", true); // otherwise sRGB becomes an sRGB chunk
            image.Write(path);
        }
        Assert.Contains("iCCP", Encoding.Latin1.GetString(File.ReadAllBytes(path)));

        var raw = RawText(WritePdf(path));

        Assert.Matches(@"/ColorSpace \[/ICCBased \d+ 0 R\]", raw);
        Assert.Contains("/N 3 /Alternate /DeviceRGB", raw);
        Assert.Contains("/ColorSpace /DeviceGray", raw); // the soft mask never takes the profile
    }

    [Fact]
    public void Pdf_DeviceRgb_WhenNoProfile()
    {
        var pdf = WritePdf(TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 200));

        var raw = RawText(pdf);
        Assert.Contains("/ColorSpace /DeviceRGB", raw);
        Assert.DoesNotContain("/ICCBased", raw);
    }

    [Fact]
    public void Pdf_MismatchedProfile_FallsBackToDevice()
    {
        // An RGB profile in a grayscale file must not become an ICCBased space with the wrong /N.
        var path = TestImages.Write(_dir, "g.jpg", 64, 48, ColorSpace.Gray, 8, 200, icc: TestImages.SrgbIcc);
        Assert.NotNull(JpegInspector.Inspect(path).IccProfile);

        var raw = RawText(WritePdf(path));

        Assert.Contains("/ColorSpace /DeviceGray", raw);
        Assert.DoesNotContain("/ICCBased", raw);
    }

    [Fact]
    public void Pdf_AdobeCmykJpeg_GetsInvertedDecode()
    {
        var path = TestImages.Write(_dir, "c.jpg", 64, 48, ColorSpace.CMYK, 8, 200);
        var info = JpegInspector.Inspect(path);
        Assert.Equal(4, info.Components);
        Assert.True(info.HasAdobeMarker);

        var pdf = WritePdf(path);

        Assert.Contains("/ColorSpace /DeviceCMYK", RawText(pdf));
        Assert.Contains("/Decode [1 0 1 0 1 0 1 0]", RawText(pdf));
        Assert.Equal([1d, 0, 1, 0, 1, 0, 1, 0], SingleImage(pdf, out _).Decode);
    }

    [Fact]
    public void Pdf_PlainCmykJpeg_NoDecode()
    {
        var path = TestImages.Write(_dir, "c.jpg", 64, 48, ColorSpace.CMYK, 8, 200);
        RemoveApp14(path);
        var info = JpegInspector.Inspect(path);
        Assert.Equal(4, info.Components);
        Assert.False(info.HasAdobeMarker);

        var pdf = WritePdf(path);

        Assert.Contains("/ColorSpace /DeviceCMYK", RawText(pdf));
        Assert.DoesNotContain("/Decode", RawText(pdf));
    }

    [Fact]
    public void Pdf_RgbJpegWithAdobeMarker_NoDecode()
    {
        var path = TestImages.Write(_dir, "a.jpg", 64, 48, ColorSpace.sRGB, 8, 200);
        JpegSegments.Insert(path, [0xFF, 0xEE, 0x00, 0x0E, .. "Adobe"u8.ToArray(), 0x00, 0x64, 0, 0, 0, 0, 1]);
        Assert.True(JpegInspector.Inspect(path).HasAdobeMarker);

        Assert.DoesNotContain("/Decode", RawText(WritePdf(path)));
    }

    [Theory]
    [InlineData(ColorSpace.sRGB, "RGB", "/DeviceRGB")]
    [InlineData(ColorSpace.Gray, "R", "/DeviceGray")]
    public void Pdf_PngAlpha_HasSMask(ColorSpace cs, string mapping, string deviceSpace)
    {
        var path = TestImages.Write(_dir, "a.png", 50, 30, cs, 8, 200, alpha: true);

        var pdf = WritePdf(path);

        var raw = RawText(pdf);
        Assert.Matches(@"/SMask \d+ 0 R", raw);
        Assert.Contains("/ColorSpace " + deviceSpace, raw);
        var image = SingleImage(pdf, out _);
        Assert.Equal(SourceSamples(path, mapping, 8), Inflate(image.RawBytes));
        Assert.NotNull(image.MaskImage);
        Assert.Equal(SourceSamples(path, "A", 8), Inflate(image.MaskImage!.RawBytes));
    }

    [Fact]
    public void Pdf_PalettePng_ExpandedToRgb()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "p.png");
        using (var image = new MagickImage(MagickColors.Red, 40, 20))
        {
            using (var blue = new MagickImage(MagickColors.Blue, 20, 20)) image.Composite(blue, 0, 0, CompositeOperator.Over);
            image.Settings.SetDefine(MagickFormat.Png, "color-type", "3");
            image.Write(path);
        }
        Assert.Equal(3, File.ReadAllBytes(path)[25]); // IHDR colour type 3 = palette

        var pdf = WritePdf(path);

        var raw = RawText(pdf);
        Assert.Contains("/ColorSpace /DeviceRGB", raw);
        Assert.DoesNotContain("/Indexed", raw);
        Assert.Equal(SourceSamples(path, "RGB", 8), Inflate(SingleImage(pdf, out _).RawBytes));
    }

    [Fact]
    public void Pdf_NoAlpha_NoSMask()
    {
        Assert.DoesNotContain("/SMask", RawText(WritePdf(TestImages.Write(_dir, "a.png", 50, 30, ColorSpace.sRGB, 8, 200))));
    }

    [Fact]
    public void Pdf_OpensInPdfPig_OnePage()
    {
        var pdf = WritePdf(TestImages.Write(_dir, "a.png", 50, 30, ColorSpace.sRGB, 8, 100), 100);

        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        Assert.Equal(1, document.NumberOfPages);
        Assert.Equal(1.6, document.Version, 3);
        Assert.StartsWith("Illux Image Tool ", document.Information.Producer);
        Assert.NotNull(document.Information.GetCreatedDateTimeOffset());
        var page = document.GetPage(1);
        var image = Assert.Single(page.GetImages());
        Assert.Equal(36, page.Width, 0.01);
        Assert.Equal(21.6, page.Height, 0.01);
        Assert.Equal(page.Width, image.BoundingBox.Width, 0.01);
        Assert.Equal(page.Height, image.BoundingBox.Height, 0.01);
        Assert.Empty(page.Letters);
        Assert.Empty(page.GetAnnotations());
    }

    [Fact]
    public void Pdf_XrefOffsetsPointToObjects()
    {
        // PdfPig repairs a broken cross-reference table silently, so check the offsets directly.
        var raw = RawText(WritePdf(TestImages.Write(_dir, "a.png", 50, 30, ColorSpace.sRGB, 8, 200, alpha: true, icc: TestImages.SrgbIcc)));

        var startxref = int.Parse(raw[(raw.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref\n0 ", raw[startxref..]);
        var lines = raw[startxref..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1]);
        Assert.Equal("0000000000 65535 f\r", lines[2]);
        for (var number = 1; number < count; number++)
        {
            var entry = lines[2 + number];
            Assert.Matches(@"^\d{10} 00000 n\r$", entry);
            Assert.StartsWith($"{number} 0 obj\n", raw[int.Parse(entry[..10])..]);
        }
        Assert.Contains($"/Size {count} ", raw);
        Assert.EndsWith("%%EOF\n", raw);
    }

    /// <summary>Sets the EXIF Orientation of <paramref name="path"/> (re-encodes it once; the PDF then embeds the result as it is).</summary>
    private static void SetOrientation(string path, int orientation)
    {
        using var image = new MagickImage(path);
        var exif = image.GetExifProfile() ?? new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        image.SetProfile(exif);
        image.Orientation = (OrientationType)orientation;
        image.Write(path);
    }

    // A 300 × 200 px image at 150 ppi is 144 × 96 pt; 5-8 turn it a quarter, so the page becomes 96 × 144 pt.
    [Theory]
    [InlineData(1, "144 96", "144 0 0 96 0 0")]
    [InlineData(2, "144 96", "-144 0 0 96 144 0")]
    [InlineData(3, "144 96", "-144 0 0 -96 144 96")]
    [InlineData(4, "144 96", "144 0 0 -96 0 96")]
    [InlineData(5, "96 144", "0 -144 -96 0 96 144")]
    [InlineData(6, "96 144", "0 -144 96 0 0 144")]
    [InlineData(7, "96 144", "0 144 96 0 0 0")]
    [InlineData(8, "96 144", "0 144 -96 0 96 0")]
    public void Pdf_Jpeg_ExifOrientation_InPageSizeAndMatrix_BytesUntouched(int orientation, string size, string matrix)
    {
        var path = TestImages.Write(_dir, $"o{orientation}.jpg", 300, 200, ColorSpace.sRGB, 8, 150);
        SetOrientation(path, orientation);

        var pdf = WritePdf(path, 150);

        var raw = RawText(pdf);
        Assert.Contains($"/MediaBox [0 0 {size}] /TrimBox [0 0 {size}]", raw);
        Assert.Contains($"q {matrix} cm /Im0 Do Q", raw);
        var image = SingleImage(pdf, out var page);
        Assert.Equal(File.ReadAllBytes(path), image.RawBytes.ToArray()); // the JPEG is still embedded byte for byte
        Assert.Equal(300, image.WidthInSamples);

        // Where the stored image's corners land (EXIF 2.32, Orientation): checked independently of the matrix text.
        var m = matrix.Split(' ').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        (double X, double Y) At(double u, double v) => (m[0] * u + m[2] * v + m[4], m[1] * u + m[3] * v + m[5]);
        var corners = new Dictionary<string, (double, double)> { ["TL"] = (0, page.Height), ["TR"] = (page.Width, page.Height), ["BL"] = (0, 0), ["BR"] = (page.Width, 0) };
        var expected = orientation switch
        {
            1 => ("TL", "TR", "BL"), 2 => ("TR", "TL", "BR"), 3 => ("BR", "BL", "TR"), 4 => ("BL", "BR", "TL"),
            5 => ("TL", "BL", "TR"), 6 => ("TR", "BR", "TL"), 7 => ("BR", "TR", "BL"), _ => ("BL", "TL", "BR"),
        };
        Assert.Equal(corners[expected.Item1], At(0, 1)); // first pixel of the first stored row
        Assert.Equal(corners[expected.Item2], At(1, 1)); // last pixel of the first stored row
        Assert.Equal(corners[expected.Item3], At(0, 0)); // first pixel of the last stored row
    }

    [Fact]
    public void Pdf_Tiff_ExifOrientation_TurnsThePage()
    {
        var path = TestImages.Write(_dir, "o6.tif", 300, 200, ColorSpace.sRGB, 8, 150);
        using (var image = new MagickImage(path))
        {
            image.Orientation = OrientationType.RightTop;
            image.Write(path);
        }

        var raw = RawText(WritePdf(path, 150));

        Assert.Contains("/MediaBox [0 0 96 144]", raw);
        Assert.Contains("q 0 -144 96 0 0 144 cm /Im0 Do Q", raw);
    }

    [Fact]
    public void Pdf_LargeTurnedPage_UserUnitAppliesToTheMatrixToo()
    {
        var path = TestImages.Write(_dir, "huge6.jpg", 64, 48, ColorSpace.sRGB, 8, 200);
        SetOrientation(path, 6);
        var bytes = File.ReadAllBytes(path);
        var sof = bytes.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(sof + 5), 20000);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(sof + 7), 40500);
        File.WriteAllBytes(path, bytes);

        var raw = RawText(WritePdf(path, 200));

        Assert.Contains("/UserUnit 2 /MediaBox [0 0 3600 7290] /TrimBox [0 0 3600 7290]", raw);
        Assert.Contains("q 0 -7290 3600 0 0 7290 cm /Im0 Do Q", raw);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0 }, 6)]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 8, 0, 0, 0, 0, 0 }, 8)]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 9, 0, 0, 0, 0, 0 }, 1)] // out of range
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 200, 0, 0, 0 }, 1)] // IFD offset past the end
    [InlineData(new byte[] { 1, 2, 3 }, 1)]
    public void ExifOrientation_FromTiff(byte[] tiff, int expected) => Assert.Equal(expected, ExifOrientation.FromTiff(tiff));

    [Fact]
    public void Pdf_UnreadableInput_LeavesNoFile()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "broken.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 0x00]); // truncated JPEG
        var pdf = Path.Combine(_dir, "broken.pdf");

        Assert.ThrowsAny<Exception>(() => PdfImageWriter.Write(path, new Resolution(200, 200), pdf));
        Assert.False(File.Exists(pdf));
    }
}
