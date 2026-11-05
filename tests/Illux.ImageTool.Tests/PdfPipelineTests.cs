using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Options;
using Illux.ImageTool.Pdf.Input;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Tests;

/// <summary>
/// <see cref="PdfPipeline"/>: scan → plan → extract → image pipeline → encode → rewrite, and its use by
/// <see cref="FileProcessor"/>. Every output is checked through PdfPig and a fresh scan: same pages and page sizes,
/// each image drawn at the same size, and the new pixel size (so the new ppi) the rules ask for.
/// </summary>
public sealed class PdfPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdfpipe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string In => Path.Combine(_dir, "in");

    private string OutDir => Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

    private string Output(string name = "out.pdf") => Path.Combine(OutDir, name);

    private string Jpeg(string name, int w = 60, int h = 40, bool progressive = false) =>
        TestImages.Write(Path.Combine(_dir, "src"), name, w, h, ColorSpace.sRGB, 8, 72, progressive: progressive);

    /// <summary>Drawn length (cm) at which <paramref name="pixels"/> give <paramref name="ppi"/>.</summary>
    private static double Cm(int pixels, double ppi) => pixels / ppi * 2.54;

    private static Task<PipelineResult> Run(string input, string output, ProcessingOptions? options = null) =>
        PdfPipeline.RunAsync(input, output, options ?? ProcessingOptions.Default, CancellationToken.None);

    private Task<FileResult> Process(string input, ProcessingOptions? options = null) =>
        FileProcessor.ProcessAsync(input, OutDir, options ?? ProcessingOptions.Default, NamePolicy.Overwrite,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);

    private string[] OutNames() => Directory.GetFileSystemEntries(OutDir).Select(p => Path.GetFileName(p)!).Order().ToArray();

    // ---- the brief's tests ----

    [Fact]
    public async Task Pdf_OneJpeg_120ppi_Large_ScaledTo200()
    {
        // Ruling Q5: 2882 × 2159 px drawn at 61 × 45.7 cm is 120 ppi; rule Large (not within 40 × 30 cm) → 200 ppi.
        var jpeg = Jpeg("big.jpg", 2882, 2159);
        var pdf = TestPdfs.ChiliLike(In, "chili.pdf", jpeg, 61, 45.7);
        var before = PdfImageScanner.Scan(pdf);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(
            ["page 1, image 1: 120→200 ppi (rule \"Large\": 61.0 × 45.7 cm; 2882×2159 → 4803×3598, bicubic)",
             "page 1, image 1: re-encoded (q95)"],
            result.Messages);
        Assert.Equal(new Resolution(200, 200), result.FinalResolution);
        AssertSamePages(pdf, Output());
        var image = AssertSameDrawnSizes(before, Output()).Single();
        var dictionary = ImageStream(Output(), image.Image).StreamDictionary;
        Assert.Equal(4803, Int(dictionary, "Width"));
        Assert.Equal(3598, Int(dictionary, "Height"));
        Assert.Equal(200, 4803 / (image.WidthCm / 2.54), 0.1);
        Assert.Equal(200, 3598 / (image.HeightCm / 2.54), 0.1);
        var info = JpegInspector.Inspect(new MemoryStream(ImageStream(Output(), image.Image).Data.ToArray()));
        Assert.Equal((4803, 3598), (info.Width, info.Height));
        Assert.Equal(JpegCoding.Baseline, info.Coding);
    }

    [Fact]
    public async Task Pdf_Png16Bit_To8BitOnly()
    {
        // 60 × 40 px drawn at 300 ppi: inside the Small rule's range, so only the bit depth changes. The soft mask keeps
        // its size, so its object stays as it was.
        var png = TestImages.Write(Path.Combine(_dir, "src"), "deep.png", 60, 40, ColorSpace.sRGB, 16, 300, alpha: true);
        var pdf = TestPdfs.OnePage(In, "deep.pdf", png, Cm(60, 300), Cm(40, 300));
        var before = PdfImageScanner.Scan(pdf);
        var id = before.Images.Single().Image;
        var maskBefore = SoftMask(pdf, id);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal("page 1, image 1: 16→8 bit", result.Messages[0]);
        Assert.Contains(result.Messages, m => m.StartsWith("page 1, image 1: ppi inside 250–350", StringComparison.Ordinal));
        AssertSamePages(pdf, Output());
        AssertSameDrawnSizes(before, Output());
        var dictionary = ImageStream(Output(), id).StreamDictionary;
        Assert.Equal(8, Int(dictionary, "BitsPerComponent"));
        Assert.Equal((60, 40), (Int(dictionary, "Width"), Int(dictionary, "Height")));
        var maskAfter = SoftMask(Output(), id);
        Assert.Equal(maskBefore.Data.ToArray(), maskAfter.Data.ToArray());
        Assert.Equal(16, Int(maskAfter.StreamDictionary, "BitsPerComponent"));
    }

    [Fact]
    public async Task Pdf_ThreePages_AllProcessed()
    {
        // Pages 1 and 3: 100 ppi, Small → 300 ppi. Page 2: 50 × 40 cm, Large → 200 ppi (upscale above 4×: Warning).
        var pdf = TestPdfs.Pages(In, "three.pdf",
            (Jpeg("1.jpg"), Cm(60, 100), Cm(40, 100)), (Jpeg("2.jpg"), 50, 40), (Jpeg("3.jpg"), Cm(60, 100), Cm(40, 100)));
        var before = PdfImageScanner.Scan(pdf);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Warning, result.Status);
        foreach (var page in new[] { 1, 2, 3 })
            Assert.Contains(result.Messages, m => m.StartsWith($"page {page}, image 1: ", StringComparison.Ordinal) && m.Contains("ppi (rule"));
        Assert.Contains("page 2, image 1: upscale factor", string.Join("\n", result.Messages));
        Assert.Equal(new Resolution(200, 200), result.FinalResolution); // the lowest final ppi
        AssertSamePages(pdf, Output());
        var images = AssertSameDrawnSizes(before, Output());
        Assert.Equal([(180, 120), (3937, 3150), (180, 120)], images.Select(i => Size(Output(), i.Image)));
    }

    [Fact]
    public async Task Pdf_NothingToDo_ByteCopy()
    {
        var pdf = TestPdfs.OnePage(In, "fine.pdf", Jpeg("fine.jpg"), Cm(60, 300), Cm(40, 300));

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.False(result.Changed);
        Assert.Equal("no change needed", result.Messages[^1]);
        Assert.Equal(File.ReadAllBytes(pdf), File.ReadAllBytes(Output()));
    }

    [Fact]
    public async Task Pdf_ReusedImage_ProcessedOnceWithWarning()
    {
        // Drawn at 100 ppi on page 1 and at 50 ppi on page 2: processed once, for the larger size (Small → 300 ppi).
        var pdf = TestPdfs.Reused(In, "reused.pdf", Jpeg("r.jpg"), (Cm(60, 100), Cm(40, 100)), (Cm(60, 50), Cm(40, 50)));
        var before = PdfImageScanner.Scan(pdf);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal("page 1, image 1: drawn at different sizes; processed for the largest (3.0 × 2.0 cm)", result.Messages[0]);
        Assert.Single(result.Messages, m => m.Contains("ppi (rule"));
        var image = AssertSameDrawnSizes(before, Output()).Single();
        Assert.Equal((360, 240), Size(Output(), image.Image));
        AssertSamePages(pdf, Output());
    }

    [Fact]
    public async Task Pdf_InlineImage_Warning()
    {
        var pdf = TestPdfs.WithInlineImage(In, "inline.pdf");

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal(["1 inline image(s) not processed", "no images found"], result.Messages);
        Assert.Equal(File.ReadAllBytes(pdf), File.ReadAllBytes(Output()));
    }

    [Fact]
    public async Task Pdf_NoImages_CopiedWithWarning()
    {
        var pdf = TestPdfs.NoImages(In, "empty.pdf");

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal(["no images found"], result.Messages);
        Assert.False(result.Changed);
        Assert.Equal(File.ReadAllBytes(pdf), File.ReadAllBytes(Output()));
    }

    [Fact]
    public async Task Pdf_OneBadImage_WholePdfError_NothingInOut()
    {
        // Page 1 is fine; page 2's image needs scaling and has a colour-key mask.
        var good = Jpeg("good.jpg");
        var bad = Jpeg("bad.jpg");
        var pdf = TestPdfs.CustomPages(In, "bad.pdf",
            (_ => JpegEntries(good), Cm(60, 100), Cm(40, 100)),
            (_ => JpegEntries(bad, " /Mask [0 10 0 10 0 10]"), Cm(60, 100), Cm(40, 100)));

        var result = await Process(pdf);

        Assert.Equal(Status.Error, result.Status);
        Assert.Equal(["page 2, image 1: a colour-key mask on an image that needs scaling is not supported"], result.Messages);
        Assert.Empty(result.Outputs);
        Assert.Empty(OutNames());
    }

    [Theory]
    [InlineData(OutputMode.Same)]
    [InlineData(OutputMode.Pdf)]
    [InlineData(OutputMode.Both)]
    public async Task Pdf_OutputBoth_GivesOnePdf(OutputMode mode)
    {
        var pdf = TestPdfs.OnePage(In, "Card.PDF", Jpeg("c.jpg"), Cm(60, 100), Cm(40, 100));

        var result = await Process(pdf, ProcessingOptions.Default with { Output = mode });

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal(["Card.PDF"], OutNames());
        Assert.Equal([Path.Combine(OutDir, "Card.PDF")], result.Outputs);
        Assert.Equal((180, 120), Size(result.Outputs[0], PdfImageScanner.Scan(result.Outputs[0]).Images.Single().Image));
        Assert.DoesNotContain(result.Messages, m => m.Contains("PDF 1.6 written"));
    }

    [Fact]
    public async Task Pdf_TooLarge_Error()
    {
        // 60 px drawn 1000 cm wide: Large → 200 ppi would be 78,740 px.
        var pdf = TestPdfs.OnePage(In, "huge.pdf", Jpeg("h.jpg"), 1000, 700);

        var result = await Process(pdf);

        Assert.Equal(Status.Error, result.Status);
        var message = Assert.Single(result.Messages);
        Assert.StartsWith("page 1, image 1: resampling ", message);
        Assert.EndsWith("the limit is 65000 px per side", message);
        Assert.Empty(OutNames());
    }

    // ---- error mapping, axes, temp files ----

    [Fact]
    public async Task Pdf_Encrypted_Error()
    {
        var result = await Process(TestPdfs.Encrypted(In, "locked.pdf"));

        Assert.Equal(["encrypted PDFs are not supported"], result.Messages);
        Assert.Equal(Status.Error, result.Status);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task Pdf_CorruptImageData_CouldNotBeRead()
    {
        var pdf = TestPdfs.CustomImage(In, "short.pdf", _ => (
            "/Type /XObject /Subtype /Image /Width 60 /Height 40 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
            TestPdfs.Deflate(new byte[10])));

        var result = await Process(pdf);

        Assert.Equal(Status.Error, result.Status);
        Assert.Equal(["the PDF could not be read (The image data is 10 bytes; 2400 are needed.)"], result.Messages);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task Pdf_JpegSizeDisagreesWithDictionary_CouldNotBeRead()
    {
        var jpeg = Jpeg("j.jpg");
        var pdf = TestPdfs.CustomImage(In, "liar.pdf", _ =>
            ("/Type /XObject /Subtype /Image /Width 61 /Height 40 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode",
             File.ReadAllBytes(jpeg)));

        var result = await Process(pdf);

        Assert.Equal(Status.Error, result.Status);
        var message = Assert.Single(result.Messages);
        Assert.StartsWith("the PDF could not be read (", message);
        Assert.Contains("60×40", message);
        Assert.Contains("61×40", message);
    }

    [Fact]
    public async Task Pdf_RewriterCannotWriteExactly_CouldNotBeRead()
    {
        // An object in an object stream with a non-ASCII name cannot be written back exactly (ruling P1b).
        var pdf = TestPdfs.WithXrefStream(In, "packed.pdf", Jpeg("p.jpg"), Cm(60, 100), Cm(40, 100), false, "<< /A#80 1 >>");

        var result = await Process(pdf);

        Assert.Equal(Status.Error, result.Status);
        var message = Assert.Single(result.Messages);
        Assert.StartsWith("the PDF could not be read (object ", message);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task Pdf_JpegWithExifOrientation6_AxesKept()
    {
        // Stored 60 × 40 px with EXIF orientation 6 (a viewer shows it 40 × 60). PDF ignores the tag, so the image's
        // width axis is the 60 px one: drawn at 100 ppi it is resampled (Small → 300 ppi), the 40 px height axis is
        // drawn at 300 ppi and kept. Turning the image would swap the axes and their factors.
        var jpeg = Jpeg("phone.jpg");
        using (var image = new MagickImage(jpeg))
        {
            var exif = image.GetExifProfile() ?? new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            image.SetProfile(exif);
            image.Orientation = OrientationType.RightTop;
            image.Write(jpeg);
        }
        var pdf = TestPdfs.OnePage(In, "phone.pdf", jpeg, Cm(60, 100), Cm(40, 300));
        var before = PdfImageScanner.Scan(pdf);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains(result.Messages, m => m.StartsWith("page 1, image 1: 100×300→300 ppi", StringComparison.Ordinal) && m.Contains("60×40 → 180×40"));
        var id = AssertSameDrawnSizes(before, Output()).Single().Image;
        Assert.Equal((180, 40), Size(Output(), id));
        var data = ImageStream(Output(), id).Data.ToArray();
        var info = JpegInspector.Inspect(new MemoryStream(data));
        Assert.Equal((180, 40), (info.Width, info.Height));
        using var written = new MagickImage(data);
        Assert.Equal(OrientationType.RightTop, written.Orientation); // the bytes keep their tag; nothing was turned
    }

    [Fact]
    public async Task Pdf_ProgressiveJpeg_BaselineLossless()
    {
        var pdf = TestPdfs.OnePage(In, "prog.pdf", Jpeg("p.jpg", progressive: true), Cm(60, 300), Cm(40, 300));

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.Contains("page 1, image 1: progressive→baseline (lossless)", result.Messages);
        var id = PdfImageScanner.Scan(Output()).Images.Single().Image;
        Assert.Equal(JpegCoding.Baseline, JpegInspector.Inspect(new MemoryStream(ImageStream(Output(), id).Data.ToArray())).Coding);
        Assert.Equal((60, 40), Size(Output(), id));
    }

    [Fact]
    public async Task Pdf_TempFilesRemoved()
    {
        var pdf = TestPdfs.Pages(In, "two.pdf", (Jpeg("1.jpg"), Cm(60, 100), Cm(40, 100)), (Jpeg("2.jpg"), Cm(60, 100), Cm(40, 100)));

        await Run(pdf, Output());

        Assert.Equal(["out.pdf"], OutNames());
    }

    [Fact]
    public void IsPdf_MagicWithinTheFirst1024Bytes()
    {
        Directory.CreateDirectory(_dir);
        string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        var plain = Write("a.bin", "%PDF-1.7\n"u8.ToArray());
        var late = Write("b.bin", [.. new byte[1019], .. "%PDF-1.4"u8.ToArray()]);
        var tooLate = Write("c.bin", [.. new byte[1020], .. "%PDF-1.4"u8.ToArray()]);
        var jpeg = Jpeg("x.jpg");

        Assert.True(SourceImage.IsPdf(plain));
        Assert.True(SourceImage.IsPdf(late));
        Assert.False(SourceImage.IsPdf(tooLate));
        Assert.False(SourceImage.IsPdf(jpeg));
    }

    // ---- helpers ----

    private static (string Entries, byte[] Data) JpegEntries(string jpeg, string extra = "")
    {
        var info = JpegInspector.Inspect(jpeg);
        return ($"/Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} /ColorSpace /DeviceRGB " +
                $"/BitsPerComponent 8 /Filter /DCTDecode{extra}", File.ReadAllBytes(jpeg));
    }

    private static StreamToken ImageStream(string pdf, PdfObjectId id)
    {
        using var document = PdfDocument.Open(pdf);
        var stream = Assert.IsType<StreamToken>(document.Structure.GetObject(new IndirectReference(id.Number, id.Generation)).Data);
        // Copy out of the document before it is closed.
        return new StreamToken(stream.StreamDictionary, stream.Data.ToArray());
    }

    private static StreamToken SoftMask(string pdf, PdfObjectId id)
    {
        var reference = Assert.IsType<IndirectReferenceToken>(ImageStream(pdf, id).StreamDictionary.Data["SMask"]);
        return ImageStream(pdf, new PdfObjectId((int)reference.Data.ObjectNumber, reference.Data.Generation));
    }

    private static (int Width, int Height) Size(string pdf, PdfObjectId id)
    {
        var dictionary = ImageStream(pdf, id).StreamDictionary;
        return (Int(dictionary, "Width"), Int(dictionary, "Height"));
    }

    private static int Int(DictionaryToken d, string key) => Assert.IsType<NumericToken>(d.Data[key]).Int;

    private static void AssertSamePages(string input, string output)
    {
        using var a = PdfDocument.Open(input);
        using var b = PdfDocument.Open(output);
        Assert.Equal(a.NumberOfPages, b.NumberOfPages);
        for (var n = 1; n <= a.NumberOfPages; n++)
        {
            Assert.Equal(a.GetPage(n).MediaBox.Bounds, b.GetPage(n).MediaBox.Bounds);
            Assert.Equal(a.GetPage(n).CropBox.Bounds, b.GetPage(n).CropBox.Bounds);
        }
    }

    /// <summary>The output draws the same image objects at the same sizes; returns its images.</summary>
    private static IReadOnlyList<PdfImageEntry> AssertSameDrawnSizes(PdfScan before, string output)
    {
        var after = PdfImageScanner.Scan(output);
        Assert.Equal(before.PageCount, after.PageCount);
        Assert.Equal(before.Images.Count, after.Images.Count);
        foreach (var (b, a) in before.Images.Zip(after.Images))
        {
            Assert.Equal((b.Image, b.FirstPage, b.FirstIndex), (a.Image, a.FirstPage, a.FirstIndex));
            Assert.Equal(b.WidthCm, a.WidthCm, 1e-6);
            Assert.Equal(b.HeightCm, a.HeightCm, 1e-6);
        }
        return after.Images;
    }
}
