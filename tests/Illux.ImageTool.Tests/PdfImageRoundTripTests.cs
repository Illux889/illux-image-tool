using System.Buffers.Binary;
using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Pdf;
using Illux.ImageTool.Pdf.Input;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Image stream → temporary image file → (scaled with <see cref="MagickImage.Resize(IMagickGeometry)"/>) → replacement
/// stream, checked through PdfPig: the new dictionary is parsed back from its PDF syntax and the data decoded.
/// </summary>
public sealed class PdfImageRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdfimg-" + Guid.NewGuid().ToString("N"));
    private string Work => Path.Combine(_dir, "work");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void RoundTrip_RgbJpeg_DctKept()
    {
        var jpeg = TestImages.Write(_dir, "rgb.jpg", 60, 45, ColorSpace.sRGB, 8, 72);
        var pdf = TestPdfs.ChiliLike(_dir, "chili.pdf", jpeg, 10, 7.5);

        var r = RoundTrip(pdf, 2, 2);

        Assert.True(r.Extracted.IsJpeg);
        Assert.EndsWith(".jpg", r.Extracted.Path);
        Assert.Equal(File.ReadAllBytes(jpeg), File.ReadAllBytes(r.Extracted.Path));
        Assert.Null(r.Extracted.SoftMaskPath);
        Assert.Null(r.Extracted.SoftMaskId);
        Assert.Null(r.Mask);

        var d = r.NewImage;
        Assert.Equal("DCTDecode", Name(d, "Filter"));
        Assert.Equal(120, Int(d, "Width"));
        Assert.Equal(90, Int(d, "Height"));
        Assert.Equal(8, Int(d, "BitsPerComponent"));
        Assert.False(d.Data.ContainsKey("DecodeParms"));
        Assert.Equal(r.OldImage.Data["ColorSpace"], d.Data["ColorSpace"]);
        Assert.Equal(r.Image.Data.Length, Int(d, "Length"));
        Assert.Equal(File.ReadAllBytes(r.ProcessedPath), r.Image.Data);
    }

    [Fact]
    public void RoundTrip_CmykJpeg_KeepsDecodeAndColours()
    {
        var jpeg = TestImages.Write(_dir, "cmyk.jpg", 64, 48, ColorSpace.CMYK, 8, 72, icc: TestImages.CmykIcc);
        var pdf = TestPdfs.ChiliLike(_dir, "chili.pdf", jpeg, 10, 7.5);

        var r = RoundTrip(pdf, 0.5, 0.5);

        var d = r.NewImage;
        Assert.True(r.OldImage.Data.ContainsKey("Decode"));
        Assert.Equal(r.OldImage.Data["Decode"], d.Data["Decode"]);
        Assert.Equal(r.OldImage.Data["ColorSpace"], d.Data["ColorSpace"]);
        Assert.Equal("DCTDecode", Name(d, "Filter"));
        Assert.Equal(32, Int(d, "Width"));
        Assert.Equal(24, Int(d, "Height"));

        // The same patch (the middle quarter) before and after, as colours through the CMYK profile.
        using var before = new MagickImage(r.Extracted.Path);
        using var after = new MagickImage(r.Image.Data);
        Assert.Equal(ColorSpace.CMYK, after.ColorSpace);
        var deltaE = DeltaE(PatchLab(before, 16, 12, 32, 24), PatchLab(after, 8, 6, 16, 12));
        Assert.True(deltaE < 2, $"ΔE {deltaE:0.00}");
    }

    [Fact]
    public void RoundTrip_Flate16BitRgb()
    {
        var png = TestImages.Write(_dir, "rgb16.png", 40, 30, ColorSpace.sRGB, 16, 72);
        var pdf = TestPdfs.OnePage(_dir, "flate16.pdf", png, 8, 6);

        var r = RoundTrip(pdf, 1.5, 1.5);

        Assert.False(r.Extracted.IsJpeg);
        Assert.EndsWith(".tif", r.Extracted.Path);
        using (var tiff = new MagickImage(r.Extracted.Path))
        {
            Assert.Equal(MagickFormat.Tiff, tiff.Format);
            Assert.Equal(16u, tiff.Depth);
            Assert.Equal(BigEndian(Samples16(tiff, "RGB")), Decode(r.OldImage, r.OldData));
        }

        var d = r.NewImage;
        Assert.Equal("FlateDecode", Name(d, "Filter"));
        Assert.Equal(16, Int(d, "BitsPerComponent"));
        Assert.Equal(60, Int(d, "Width"));
        Assert.Equal(45, Int(d, "Height"));
        Assert.Equal("DeviceRGB", Name(d, "ColorSpace"));
        using var processed = new MagickImage(r.ProcessedPath);
        Assert.Equal(BigEndian(Samples16(processed, "RGB")), Decode(d, r.Image.Data));
    }

    [Fact]
    public void RoundTrip_FlateCmyk_SamplesUnchanged()
    {
        var tif = TestImages.Write(_dir, "cmyk.tif", 30, 20, ColorSpace.CMYK, 8, 72);
        var pdf = TestPdfs.OnePage(_dir, "cmyk.pdf", tif, 6, 4);

        var r = RoundTrip(pdf, 1, 1);

        var original = Decode(r.OldImage, r.OldData);
        using (var tiff = new MagickImage(r.Extracted.Path))
        {
            Assert.Equal(ColorSpace.CMYK, tiff.ColorSpace);
            using var pixels = tiff.GetPixels();
            Assert.Equal(original, pixels.ToByteArray("CMYK"));
        }
        Assert.Equal("DeviceCMYK", Name(r.NewImage, "ColorSpace"));
        Assert.Equal(original, Decode(r.NewImage, r.Image.Data));
    }

    [Fact]
    public void RoundTrip_GrayIcc_KeepsProfile()
    {
        var samples = new byte[20 * 10];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 20 * 12 + i / 20);
        var pdf = TestPdfs.CustomImage(_dir, "grayicc.pdf", w =>
        {
            var icc = TestPdfs.WriteStream(w, "/N 1 /Alternate /DeviceGray /Filter /FlateDecode", TestPdfs.Deflate(TestImages.GrayIcc));
            return ($"/Type /XObject /Subtype /Image /Width 20 /Height 10 /ColorSpace [/ICCBased {icc} 0 R] " +
                    "/BitsPerComponent 8 /Filter /FlateDecode", TestPdfs.Deflate(samples));
        });

        var r = RoundTrip(pdf, 2, 2);

        using (var tiff = new MagickImage(r.Extracted.Path))
        {
            Assert.Equal(ColorSpace.Gray, tiff.ColorSpace);
            Assert.Equal(8u, tiff.Depth);
            Assert.Equal(TestImages.GrayIcc, tiff.GetColorProfile()?.ToByteArray());
            using var pixels = tiff.GetPixels();
            Assert.Equal(samples, pixels.ToByteArray("R"));
        }

        var d = r.NewImage;
        Assert.Equal(r.OldImage.Data["ColorSpace"], d.Data["ColorSpace"]);
        Assert.IsType<IndirectReferenceToken>(Assert.IsType<ArrayToken>(d.Data["ColorSpace"]).Data[1]);
        Assert.Equal(8, Int(d, "BitsPerComponent"));
        Assert.Equal(40, Int(d, "Width"));
        Assert.Equal(20, Int(d, "Height"));
        using var processed = new MagickImage(r.ProcessedPath);
        using var processedPixels = processed.GetPixels();
        Assert.Equal(processedPixels.ToByteArray("R"), Decode(d, r.Image.Data));
    }

    private static readonly byte[][] Palette = [[0xFF, 0, 0], [0, 0xC0, 0], [0x10, 0x20, 0xFF], [0x80, 0x80, 0x80]];

    /// <summary>A 5 × 4 image of 2-bit indices (rows padded to whole bytes); index = (x + y) % 4.</summary>
    private string IndexedPdf(string name, string decode = "")
    {
        var data = new byte[4 * 2];
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 5; x++)
                data[y * 2 + x / 4] |= (byte)(((x + y) % 4) << (6 - x % 4 * 2));
        var lookup = string.Concat(Palette.SelectMany(c => c).Select(b => b.ToString("X2")));
        return TestPdfs.CustomImage(_dir, name, _ =>
            ($"/Type /XObject /Subtype /Image /Width 5 /Height 4 /ColorSpace [/Indexed /DeviceRGB 3 <{lookup}>] " +
             $"/BitsPerComponent 2{decode} /Filter /FlateDecode", TestPdfs.Deflate(data)));
    }

    [Fact]
    public void RoundTrip_Indexed_ExpandedSameColours()
    {
        var r = RoundTrip(IndexedPdf("indexed.pdf"), 2, 2, FilterType.Point);

        using (var tiff = new MagickImage(r.Extracted.Path))
        {
            Assert.Equal(8u, tiff.Depth);
            using var pixels = tiff.GetPixels();
            var rgb = pixels.ToByteArray("RGB")!;
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 5; x++)
                    Assert.Equal(Palette[(x + y) % 4], rgb.AsSpan((y * 5 + x) * 3, 3).ToArray());
        }

        var d = r.NewImage;
        Assert.Equal("DeviceRGB", Name(d, "ColorSpace"));
        Assert.Equal(8, Int(d, "BitsPerComponent"));
        Assert.False(d.Data.ContainsKey("Decode"));
        Assert.Equal(10, Int(d, "Width"));
        Assert.Equal(8, Int(d, "Height"));
        var samples = Decode(d, r.Image.Data);
        Assert.Equal(10 * 8 * 3, samples.Length);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 10; x++)
                Assert.Equal(Palette[(x / 2 + y / 2) % 4], samples.AsSpan((y * 10 + x) * 3, 3).ToArray());
    }

    [Fact]
    public void Extract_IndexedWithDecode_MapsIndices()
    {
        var pdf = IndexedPdf("indexed-decode.pdf", " /Decode [3 0]");
        var id = ImageId(pdf);
        using var doc = PdfDocument.Open(pdf);

        var extracted = PdfImageExtractor.Extract(doc, id, needsScaling: true, Work);

        using var tiff = new MagickImage(extracted.Path);
        using var pixels = tiff.GetPixels();
        var rgb = pixels.ToByteArray("RGB")!;
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 5; x++)
                Assert.Equal(Palette[3 - (x + y) % 4], rgb.AsSpan((y * 5 + x) * 3, 3).ToArray());
    }

    [Fact]
    public void RoundTrip_SoftMask_ScaledBySameFactors()
    {
        var png = TestImages.Write(_dir, "alpha.png", 40, 30, ColorSpace.sRGB, 8, 72, alpha: true);
        var pdf = TestPdfs.OnePage(_dir, "alpha.pdf", png, 8, 6);

        var r = RoundTrip(pdf, 0.5, 2);

        var maskRef = Assert.IsType<IndirectReferenceToken>(r.OldImage.Data["SMask"]).Data;
        Assert.Equal(new PdfObjectId((int)maskRef.ObjectNumber, maskRef.Generation), r.Extracted.SoftMaskId);
        using (var maskTiff = new MagickImage(r.Extracted.SoftMaskPath!))
        {
            Assert.Equal(ColorSpace.Gray, maskTiff.ColorSpace);
            Assert.Equal((40u, 30u), (maskTiff.Width, maskTiff.Height));
        }

        var d = r.NewImage;
        Assert.Equal((20, 60), (Int(d, "Width"), Int(d, "Height")));
        Assert.Equal(r.OldImage.Data["SMask"], d.Data["SMask"]);

        Assert.NotNull(r.Mask);
        var m = Parse(r.Mask.Dictionary);
        Assert.Equal((20, 60), (Int(m, "Width"), Int(m, "Height")));
        Assert.Equal("FlateDecode", Name(m, "Filter"));
        Assert.Equal("DeviceGray", Name(m, "ColorSpace"));
        Assert.Equal(8, Int(m, "BitsPerComponent"));
        Assert.Equal(r.Mask.Data.Length, Int(m, "Length"));
        var alpha = Decode(m, r.Mask.Data);
        Assert.Equal(20 * 60, alpha.Length);
        // The alpha ramps from left to right in every row, as before.
        for (var y = 0; y < 60; y += 7) Assert.True(alpha[y * 20] < 30 && alpha[y * 20 + 19] > 225);
    }

    [Fact]
    public void Extract_Matte_Throws()
    {
        var pdf = TestPdfs.CustomImage(_dir, "matte.pdf", w =>
        {
            var mask = TestPdfs.WriteStream(w,
                "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Matte [0 0 0]",
                [0, 64, 128, 255]);
            return ($"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R",
                new byte[12]);
        });

        var ex = Assert.Throws<UnsupportedImageException>(() => Extract(pdf, needsScaling: false));

        Assert.Equal("a soft mask with /Matte is not supported", ex.Message);
    }

    [Fact]
    public void Extract_ImageMaskNeedingScale_Throws()
    {
        var pdf = TestPdfs.CustomImage(_dir, "stencil.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 8 /Height 2 /ImageMask true /BitsPerComponent 1", [0b1010_1010, 0b0101_0101]));

        var ex = Assert.Throws<UnsupportedImageException>(() => Extract(pdf, needsScaling: true));
        Assert.Equal("a stencil mask that needs scaling is not supported", ex.Message);

        var extracted = Extract(pdf, needsScaling: false);
        using var tiff = new MagickImage(extracted.Path);
        Assert.Equal((8u, 2u), (tiff.Width, tiff.Height));
    }

    [Fact]
    public void RoundTrip_ImageMaskUnscaled_StaysOneBit()
    {
        byte[] bits = [0b1010_1010, 0b1000_0000, 0b0101_0101, 0b0000_0000]; // 9 × 2, rows padded to 2 bytes
        var pdf = TestPdfs.CustomImage(_dir, "stencil1.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 9 /Height 2 /ImageMask true /Decode [1 0] /BitsPerComponent 1", bits));
        var id = ImageId(pdf);
        using var doc = PdfDocument.Open(pdf);

        var extracted = PdfImageExtractor.Extract(doc, id, needsScaling: false, Work);
        var (image, mask) = PdfImageEncoder.Encode(doc, id, extracted.Path, extracted, (1, 1));

        Assert.Null(mask);
        var d = Parse(image.Dictionary);
        Assert.Equal(1, Int(d, "BitsPerComponent"));
        Assert.True(Assert.IsType<BooleanToken>(d.Data["ImageMask"]).Data);
        Assert.False(d.Data.ContainsKey("ColorSpace"));
        Assert.Equal(bits, Decode(d, image.Data));
    }

    [Fact]
    public void Extract_ColourKeyMaskNeedingScale_Throws()
    {
        var pdf = TestPdfs.CustomImage(_dir, "colourkey.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask [0 10]",
                [0, 64, 128, 255]));

        var ex = Assert.Throws<UnsupportedImageException>(() => Extract(pdf, needsScaling: true));
        Assert.Equal("a colour-key mask on an image that needs scaling is not supported", ex.Message);

        Assert.False(Extract(pdf, needsScaling: false).IsJpeg);
    }

    [Fact]
    public void Extract_JpxWithOwnMask_Throws()
    {
        var pdf = TestPdfs.CustomImage(_dir, "jpx.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 2 /Height 2 /Filter /JPXDecode /SMaskInData 1", [0, 0, 0, 12]));

        var ex = Assert.Throws<UnsupportedImageException>(() => Extract(pdf, needsScaling: false));

        Assert.Equal("JPEG 2000 with its own mask is not supported", ex.Message);
    }

    [Fact]
    public void RoundTrip_Jpx_BecomesLosslessFlate()
    {
        var png = TestImages.Write(_dir, "jpx.png", 24, 16, ColorSpace.sRGB, 8, 72);
        byte[] jp2;
        using (var source = new MagickImage(png))
        {
            source.Settings.SetDefine(MagickFormat.Jp2, "quality", "0"); // lossless
            jp2 = source.ToByteArray(MagickFormat.Jp2);
        }
        var pdf = TestPdfs.CustomImage(_dir, "jpx.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 24 /Height 16 /ColorSpace /DeviceRGB /Filter /JPXDecode", jp2));

        var r = RoundTrip(pdf, 1, 1);

        Assert.False(r.Extracted.IsJpeg);
        var d = r.NewImage;
        Assert.Equal("FlateDecode", Name(d, "Filter"));
        Assert.Equal("DeviceRGB", Name(d, "ColorSpace"));
        Assert.Equal(8, Int(d, "BitsPerComponent"));
        using var decoded = new MagickImage(jp2);
        using var pixels = decoded.GetPixels();
        Assert.Equal(pixels.ToByteArray("RGB"), Decode(d, r.Image.Data));
    }

    [Fact]
    public void Extract_Jbig2_Throws()
    {
        var pdf = TestPdfs.CustomImage(_dir, "jbig2.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode",
                [0, 1, 2, 3]));

        var ex = Assert.Throws<UnsupportedImageException>(() => Extract(pdf, needsScaling: false));

        Assert.Equal("JBIG2Decode image data is not supported", ex.Message);
    }

    [Fact]
    public void Encode_UnchangedKeysPreserved()
    {
        var samples = Enumerable.Range(0, 4 * 3 * 3).Select(i => (byte)(i * 7)).ToArray();
        var pdf = TestPdfs.CustomImage(_dir, "keys.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 4 /Height 3 /ColorSpace /DeviceRGB /BitsPerComponent 8 " +
             "/Interpolate true /Intent /Perceptual /Decode [1 0 0 1 0 1] /Name /Im7 /IlluxCustom (keep me) /IlluxNumber 1.5 " +
             "/Filter /FlateDecode /DecodeParms << /Predictor 1 >>", TestPdfs.Deflate(samples)));

        var r = RoundTrip(pdf, 2, 2);

        string[] changed = ["Width", "Height", "BitsPerComponent", "Filter", "DecodeParms", "Length"];
        foreach (var (key, value) in r.OldImage.Data.Where(e => !changed.Contains(e.Key)))
            Assert.Equal(value, r.NewImage.Data[key]);
        Assert.Equal(r.OldImage.Data.Keys.Where(k => k != "DecodeParms").Order(), r.NewImage.Data.Keys.Order());
        Assert.Equal((8, 6), (Int(r.NewImage, "Width"), Int(r.NewImage, "Height")));
        Assert.Equal("FlateDecode", Name(r.NewImage, "Filter"));
        Assert.Equal(r.Image.Data.Length, Int(r.NewImage, "Length"));
    }

    // ---- helpers ----

    private sealed record Result(ExtractedImage Extracted, string ProcessedPath, PdfImageStream Image, PdfImageStream? Mask,
        DictionaryToken OldImage, byte[] OldData)
    {
        public DictionaryToken NewImage { get; } = Parse(Image.Dictionary);
    }

    private static PdfObjectId ImageId(string pdf) => Assert.Single(PdfImageScanner.Scan(pdf).Images).Image;

    private ExtractedImage Extract(string pdf, bool needsScaling)
    {
        var id = ImageId(pdf);
        using var doc = PdfDocument.Open(pdf);
        return PdfImageExtractor.Extract(doc, id, needsScaling, Work);
    }

    /// <summary>Extracts the PDF's only image, scales it by exactly (fx, fy) and encodes it again.</summary>
    private Result RoundTrip(string pdf, double fx, double fy, FilterType filter = FilterType.Catrom)
    {
        var id = ImageId(pdf);
        using var doc = PdfDocument.Open(pdf);
        var old = Assert.IsType<StreamToken>(doc.Structure.GetObject(new IndirectReference(id.Number, id.Generation)).Data);
        var extracted = PdfImageExtractor.Extract(doc, id, needsScaling: true, Work);
        var processed = Scale(extracted.Path, fx, fy, filter);
        var (image, mask) = PdfImageEncoder.Encode(doc, id, processed, extracted, (fx, fy));
        return new Result(extracted, processed, image, mask, old.StreamDictionary, old.Data.ToArray());
    }

    private static string Scale(string path, double fx, double fy, FilterType filter)
    {
        using var image = new MagickImage(path);
        var colorType = image.ColorType;
        var compression = image.Compression;
        var format = image.Format;
        var width = (uint)Math.Round(image.Width * fx);
        var height = (uint)Math.Round(image.Height * fy);
        Assert.Equal(image.Width * fx, width, 1e-9);
        Assert.Equal(image.Height * fy, height, 1e-9);
        image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true }, filter);
        image.Settings.ColorType = colorType == ColorType.Bilevel ? ColorType.Grayscale : colorType;
        image.Settings.Compression = compression;
        var output = Path.Combine(Path.GetDirectoryName(path)!, "scaled-" + Path.GetFileName(path));
        image.Write(output, format);
        return output;
    }

    private static DictionaryToken Parse(byte[] dictionary)
    {
        var scanner = new CoreTokenScanner(new MemoryInputBytes(dictionary), true, new StackDepthGuard(64));
        Assert.True(scanner.MoveNext());
        var token = Assert.IsType<DictionaryToken>(scanner.CurrentToken);
        Assert.False(scanner.MoveNext());
        return token;
    }

    private static byte[] Decode(DictionaryToken dictionary, byte[] data) =>
        new StreamToken(dictionary, data).Decode(DefaultFilterProvider.Instance).ToArray();

    private static int Int(DictionaryToken d, string key) => Assert.IsType<NumericToken>(d.Data[key]).Int;

    private static string Name(DictionaryToken d, string key) => Assert.IsType<NameToken>(d.Data[key]).Data;

    private static ushort[] Samples16(MagickImage image, string mapping)
    {
        using var pixels = image.GetPixels();
        return pixels.ToShortArray(mapping)!;
    }

    private static byte[] BigEndian(ushort[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    /// <summary>The mean colour of a CMYK patch as CIE L*a*b* (D65), through the SWOP profile and sRGB.</summary>
    private static (double L, double A, double B) PatchLab(MagickImage cmyk, int x, int y, uint w, uint h)
    {
        using var patch = (MagickImage)cmyk.Clone();
        patch.Crop(new MagickGeometry(x, y, w, h));
        patch.RemoveProfile("icc");
        patch.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
        var stats = patch.Statistics();
        double Mean(PixelChannel c) => stats.GetChannel(c)!.Mean / 65535;
        return Lab(Mean(PixelChannel.Red), Mean(PixelChannel.Green), Mean(PixelChannel.Blue));
    }

    private static (double L, double A, double B) Lab(double r, double g, double b)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        double lr = Linear(r), lg = Linear(g), lb = Linear(b);
        var x = (0.4124 * lr + 0.3576 * lg + 0.1805 * lb) / 0.95047;
        var y = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
        var z = (0.0193 * lr + 0.1192 * lg + 0.9505 * lb) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    private static double DeltaE((double L, double A, double B) p, (double L, double A, double B) q) =>
        Math.Sqrt(Math.Pow(p.L - q.L, 2) + Math.Pow(p.A - q.A, 2) + Math.Pow(p.B - q.B, 2));
}
