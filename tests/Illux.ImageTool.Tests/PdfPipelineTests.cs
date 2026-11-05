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
        PdfCheckOutput.Keep(_dir, "pipeline");
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
        // 60 × 40 px drawn at 300 ppi: inside the Small rule's range, so only the bit depth changes. Ruling Q13: the
        // soft mask follows its image to 8 bits, keeps its size and its own object.
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
        Assert.Equal(MaskId(pdf, id), MaskId(Output(), id));
        Assert.Equal(16, Int(maskBefore.StreamDictionary, "BitsPerComponent"));
        Assert.Equal(8, Int(maskAfter.StreamDictionary, "BitsPerComponent"));
        Assert.Equal((60, 40), (Int(maskAfter.StreamDictionary, "Width"), Int(maskAfter.StreamDictionary, "Height")));
        var deep = Inflate(maskBefore.Data.ToArray());
        var shallow = Inflate(maskAfter.Data.ToArray());
        Assert.Equal(60 * 40, shallow.Length);
        for (var i = 0; i < shallow.Length; i++)
            Assert.InRange(shallow[i] - ((deep[2 * i] << 8) | deep[2 * i + 1]) / 257.0, -0.51, 0.51);
    }

    [Fact]
    public async Task Pdf_EightBitOff_SoftMaskKeepsItsDepth()
    {
        // Q13 follows the operation: with only resample, a scaled 16-bit image and its mask stay 16 bit.
        var png = TestImages.Write(Path.Combine(_dir, "src"), "deep.png", 60, 40, ColorSpace.sRGB, 16, 300, alpha: true);
        var pdf = TestPdfs.OnePage(In, "deep.pdf", png, Cm(60, 100), Cm(40, 100));
        var id = PdfImageScanner.Scan(pdf).Images.Single().Image;

        var result = await Run(pdf, Output(), ProcessingOptions.Default with { Operations = Operations.Resample });

        Assert.Equal(Status.Ok, result.Status);
        var mask = SoftMask(Output(), id).StreamDictionary;
        Assert.Equal(16, Int(mask, "BitsPerComponent"));
        Assert.Equal((180, 120), (Int(mask, "Width"), Int(mask, "Height")));
        Assert.Equal(16, Int(ImageStream(Output(), id).StreamDictionary, "BitsPerComponent"));
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

        // Only the inline-images warning: the PDF does have an image, so "no images found" would contradict it.
        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal(["1 inline image(s) not processed"], result.Messages);
        Assert.Equal(File.ReadAllBytes(pdf), File.ReadAllBytes(Output()));
    }

    [Theory]
    [InlineData("annotation")]
    [InlineData("pattern")]
    public async Task Pdf_ImageNotDrawnOnAPage_Warning(string where)
    {
        // Ruling Q16: an image the walk does not reach is not skipped silently.
        var pdf = TestPdfs.WithUndrawnImage(In, where + ".pdf", Jpeg("u.jpg"), where);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal(["1 image(s) not drawn on a page were not processed"], result.Messages);
        Assert.Equal(File.ReadAllBytes(pdf), File.ReadAllBytes(Output()));
    }

    [Fact]
    public async Task Pdf_PageThumbnail_NoWarning()
    {
        var pdf = TestPdfs.WithUndrawnImage(In, "thumb.pdf", Jpeg("t.jpg"), "thumb");

        var result = await Run(pdf, Output());

        Assert.DoesNotContain(result.Messages, m => m.Contains("not drawn", StringComparison.Ordinal));
        Assert.True(result.Changed); // 60 px drawn 5 cm wide: resampled
    }

    [Fact]
    public async Task Pdf_ReusedAtCrossedSizes_PerAxisMaximumInTheWarning()
    {
        // Ruling Q17: 10 × 30 cm on page 1, 20 × 10 cm on page 2 → processed for 20 × 30 cm.
        var pdf = TestPdfs.Reused(In, "crossed.pdf", Jpeg("x.jpg", 600, 900), (10, 30), (20, 10));

        var result = await Run(pdf, Output());

        Assert.Equal("page 1, image 1: drawn at different sizes; processed for the largest (20.0 × 30.0 cm)", result.Messages[0]);
        var id = PdfImageScanner.Scan(Output()).Images.Single().Image;
        Assert.Equal((2362, 3543), Size(Output(), id)); // 300 ppi (rule Small: 20 × 30 fits 40 × 30) along 20 and 30 cm
    }

    [Fact]
    public async Task Pdf_BlankSignatureField_Processed()
    {
        // Ruling Q6a: /SigFlags without a signature dictionary is a blank field; the file is not refused.
        var pdf = TestPdfs.Signed(In, "blank.pdf");

        var result = await Process(pdf);

        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal(["no images found"], result.Messages);
        Assert.Equal(["blank.pdf"], OutNames());
    }

    [Fact]
    public async Task Pdf_SignedWithByteRange_Error()
    {
        var result = await Process(TestPdfs.Signed(In, "signed.pdf", byteRange: true));

        Assert.Equal(Status.Error, result.Status);
        Assert.Equal(["signed PDFs are not supported"], result.Messages);
        Assert.Empty(OutNames());
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

    // ---- fix round 1 ----

    /// <summary>Records the pipeline's test hook events while <paramref name="action"/> runs.</summary>
    private static async Task<List<(string Stage, string Path)>> Hooked(Func<Task> action, Action<string, string>? also = null)
    {
        var events = new List<(string, string)>();
        PdfPipeline.TestHook.Value = (stage, path) =>
        {
            lock (events) events.Add((stage, path));
            also?.Invoke(stage, path);
        };
        try
        {
            await action();
        }
        finally
        {
            PdfPipeline.TestHook.Value = null;
        }
        return events;
    }

    public static TheoryData<string, string> CheapRejections => new()
    {
        { "colourkey", "a colour-key mask on an image that needs scaling is not supported" },
        { "stencil", "a stencil mask that needs scaling is not supported" },
        { "smaskindata", "JPEG 2000 with its own mask is not supported" },
        { "matte", "a soft mask with /Matte is not supported" },
        { "pattern", "the Pattern colour space is not supported" },
        { "devicen2", "a colour space with 2 components is not supported" },
        { "jbig2", "JBIG2Decode image data is not supported" },
    };

    [Theory]
    [MemberData(nameof(CheapRejections))]
    public async Task Pdf_BadImageOnPage2_FailsWhilePlanning_NothingResampled(string kind, string what)
    {
        // M4: page 1 needs scaling and is fine; page 2 needs scaling and cannot be processed. Nothing is resampled.
        var good = Jpeg("good.jpg");
        const string Head = "/Type /XObject /Subtype /Image /Width 60 /Height 40";
        var rgb = TestPdfs.Deflate(new byte[60 * 40 * 3]);
        Func<Illux.ImageTool.Pdf.PdfObjectWriter, (string, byte[])> bad = kind switch
        {
            "colourkey" => _ => JpegEntries(Jpeg("bad.jpg"), " /Mask [0 10 0 10 0 10]"),
            "stencil" => _ => ($"{Head} /ImageMask true /Filter /FlateDecode", TestPdfs.Deflate(new byte[8 * 40])),
            "smaskindata" => _ => ($"{Head} /ColorSpace /DeviceRGB /Filter /JPXDecode /SMaskInData 1", [0, 0, 0, 12]),
            "matte" => w =>
            {
                var mask = TestPdfs.WriteStream(w, $"{Head} /ColorSpace /DeviceGray /BitsPerComponent 8 /Matte [0 0 0] /Filter /FlateDecode",
                    TestPdfs.Deflate(new byte[60 * 40]));
                return ($"{Head} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {mask} 0 R /Filter /FlateDecode", rgb);
            },
            "pattern" => _ => ($"{Head} /ColorSpace /Pattern /BitsPerComponent 8 /Filter /FlateDecode", rgb),
            "devicen2" => _ => ($"{Head} /ColorSpace [/DeviceN [/A /B] /DeviceCMYK null] /BitsPerComponent 8 /Filter /FlateDecode", rgb),
            "jbig2" => _ => ($"{Head} /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode", [0, 0, 0, 0]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var pdf = TestPdfs.CustomPages(In, "bad.pdf",
            (_ => JpegEntries(good), Cm(60, 100), Cm(40, 100)), (bad, Cm(60, 100), Cm(40, 100)));

        FileResult? result = null;
        var events = await Hooked(async () => result = await Process(pdf));

        Assert.Equal([$"page 2, image 1: {what}"], result!.Messages);
        Assert.Equal(Status.Error, result.Status);
        Assert.Empty(events);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task Pdf_DocumentClosedWhileTheImageIsResampled()
    {
        // M3: nothing holds the input open while the image pipeline runs (FileShare.None would fail otherwise).
        var pdf = TestPdfs.OnePage(In, "a.pdf", Jpeg("a.jpg"), Cm(60, 100), Cm(40, 100));
        var exclusive = new List<bool>();

        var events = await Hooked(() => Run(pdf, Output()), (stage, path) =>
        {
            if (stage != "resample") return;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) exclusive.Add(true);
            }
            catch (IOException)
            {
                exclusive.Add(false);
            }
        });

        Assert.Equal([true], exclusive);
        Assert.Single(events, e => e.Stage == "resample");
    }

    [Fact]
    public async Task Pdf_TempFilesOfAnImageAreGoneBeforeTheNextStarts_OnlyItsDataStays()
    {
        // Final review I2: an image's work files are gone before the next image starts; only its encoded data waits,
        // in a file under data/, until the rewrite (which still finds every one there).
        var pdf = TestPdfs.Pages(In, "three.pdf",
            (Jpeg("1.jpg"), Cm(60, 100), Cm(40, 100)), (Jpeg("2.jpg"), Cm(60, 100), Cm(40, 100)), (Jpeg("3.jpg"), Cm(60, 100), Cm(40, 100)));
        var leftOver = new List<(string Stage, int Work, int Data)>();
        string? tempRoot = null;

        await Hooked(() => Run(pdf, Output()), (stage, root) =>
        {
            if (stage is not ("image" or "rewrite")) return;
            tempRoot = root;
            var data = Path.Combine(root, "data");
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            leftOver.Add((stage, files.Count(f => !f.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
                files.Count(f => f.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal))));
        });

        Assert.Equal([("image", 0, 0), ("image", 0, 1), ("image", 0, 2), ("rewrite", 0, 3)], leftOver);
        Assert.False(Directory.Exists(tempRoot));
        Assert.Equal(["out.pdf"], OutNames());
        Assert.Equal([(180, 120), (180, 120), (180, 120)], PdfImageScanner.Scan(Output()).Images.Select(i => Size(Output(), i.Image)));
    }

    [Fact]
    public async Task Pdf_SoftMaskedImageScaled_MaskUnderItsOwnIdLinesUp()
    {
        // Q3: an 8-bit PNG with alpha (image + soft mask) drawn at 100 ppi → 300 ppi: both become 180 × 120.
        var png = TestImages.Write(Path.Combine(_dir, "src"), "alpha.png", 60, 40, ColorSpace.sRGB, 8, 72, alpha: true);
        var pdf = TestPdfs.OnePage(In, "alpha.pdf", png, Cm(60, 100), Cm(40, 100));
        var before = PdfImageScanner.Scan(pdf);
        var id = before.Images.Single().Image;

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        AssertSamePages(pdf, Output());
        AssertSameDrawnSizes(before, Output());
        Assert.Equal((180, 120), Size(Output(), id));
        Assert.Equal(MaskId(pdf, id), MaskId(Output(), id));
        var mask = SoftMask(Output(), id).StreamDictionary;
        Assert.Equal((180, 120), (Int(mask, "Width"), Int(mask, "Height")));
        Assert.Equal("/FlateDecode", mask.Data["Filter"].ToString());
        AssertNonImageStreamsSame(pdf, Output());
    }

    [Fact]
    public async Task Pdf_SharedSoftMask_ReplacedOnceUnderItsId()
    {
        // Two images share one soft mask: page 1 is scaled 3× (100 → 300 ppi), page 2 2× (150 → 300 ppi). The mask is
        // scaled once, with the first image; a soft mask is mapped onto each image whatever its pixel size.
        const string Head = "/Type /XObject /Subtype /Image /Width 60 /Height 40 /BitsPerComponent 8 /Filter /FlateDecode";
        var mask = 0;
        var pdf = TestPdfs.CustomPages(In, "shared.pdf",
            (w =>
            {
                mask = TestPdfs.WriteStream(w, $"{Head} /ColorSpace /DeviceGray",
                    TestPdfs.Deflate(Enumerable.Range(0, 2400).Select(i => (byte)i).ToArray()));
                return ($"{Head} /ColorSpace /DeviceRGB /SMask {mask} 0 R", TestPdfs.Deflate(new byte[60 * 40 * 3]));
            }, Cm(60, 100), Cm(40, 100)),
            (_ => ($"{Head} /ColorSpace /DeviceRGB /SMask {mask} 0 R", TestPdfs.Deflate(new byte[60 * 40 * 3])), Cm(60, 150), Cm(40, 150)));
        var images = PdfImageScanner.Scan(pdf).Images;

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal([(180, 120), (120, 80)], images.Select(i => Size(Output(), i.Image)));
        Assert.All(images, i => Assert.Equal(new PdfObjectId(mask, 0), MaskId(Output(), i.Image)));
        var maskDictionary = ImageStream(Output(), new PdfObjectId(mask, 0)).StreamDictionary;
        Assert.Equal((180, 120), (Int(maskDictionary, "Width"), Int(maskDictionary, "Height")));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(30)]
    public async Task Pdf_RotatedImage_ScaledAlongItsOwnAxes(double degrees)
    {
        // 60 × 40 px drawn 1.524 × 1.016 cm along its axes (100 ppi), turned on the page: 180 × 120 px at 300 ppi.
        var pdf = TestPdfs.OnePage(In, "turned.pdf", Jpeg("t.jpg"), Cm(60, 100), Cm(40, 100), rotateDegrees: degrees);
        var before = PdfImageScanner.Scan(pdf);

        var result = await Run(pdf, Output());

        Assert.Equal(Status.Ok, result.Status);
        Assert.StartsWith("page 1, image 1: 100→300 ppi", result.Messages[0]);
        AssertSamePages(pdf, Output());
        var image = AssertSameDrawnSizes(before, Output()).Single();
        Assert.Equal((180, 120), Size(Output(), image.Image));
        AssertNonImageStreamsSame(pdf, Output());
    }

    [Fact]
    public async Task Pdf_ContentStreamsUnchanged()
    {
        // Spec 7: the drawing instructions (page content, form content) keep their bytes.
        var chili = TestPdfs.ChiliLike(In, "chili.pdf", Jpeg("c.jpg"), Cm(60, 100), Cm(40, 100));
        var inForm = TestPdfs.InForm(In, "form.pdf", Jpeg("f.jpg"), Cm(60, 100), Cm(40, 100));

        await Run(chili, Output("chili.pdf"));
        await Run(inForm, Output("form.pdf"));

        Assert.True(AssertNonImageStreamsSame(chili, Output("chili.pdf")) >= 3); // page content, form, ICC profile
        Assert.True(AssertNonImageStreamsSame(inForm, Output("form.pdf")) >= 2); // page content, form
        var id = PdfImageScanner.Scan(chili).Images.Single().Image;
        Assert.Equal((180, 120), Size(Output("chili.pdf"), id)); // and the image did change
    }

    [Fact]
    public async Task Pdf_LongPath_Works()
    {
        // The per-image temp files sit deeper than the output; all Magick reads go through .NET IO.
        var deep = Path.Combine(OutDir, new string('d', 90), new string('e', 90), new string('f', 90));
        Directory.CreateDirectory(deep);
        var png = TestImages.Write(Path.Combine(_dir, "src"), "p.png", 60, 40, ColorSpace.sRGB, 8, 72);
        var pdf = TestPdfs.OnePage(In, "p.pdf", png, Cm(60, 100), Cm(40, 100));
        var output = Path.Combine(deep, "out.pdf");
        Assert.True(output.Length > 300);

        var result = await Run(pdf, output);

        Assert.Equal(Status.Ok, result.Status);
        Assert.Equal((180, 120), Size(output, PdfImageScanner.Scan(output).Images.Single().Image));
    }

    [Fact]
    public async Task Pdf_IllustratorPrivateData_Error()
    {
        var pdf = TestPdfs.Classic(In, "aiprivatedata.pdf",
            (4, "4 0 obj\n<< /Type /Private >>\nendobj"), (5, "5 0 obj\n<< /AIPrivateData1 (x) /NumBlock 1 >>\nendobj"));

        var result = await Process(pdf);

        Assert.Equal(["Illustrator files are not supported"], result.Messages);
        Assert.Equal(Status.Error, result.Status);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task Pdf_PieceInfoIllustratorAlone_Processed()
    {
        // Ruling Q12a: /PieceInfo /Illustrator without AIPrivateData (no private second copy) is processed like any PDF.
        var pdf = TestPdfs.Classic(In, "pieceinfo.pdf",
            (4, "4 0 obj\n<< /PieceInfo << /Illustrator << /LastModified (D:20260101) /Private 5 0 R >> >> >>\nendobj"),
            (5, "5 0 obj\n<< /Type /Private >>\nendobj"));

        var result = await Process(pdf);

        Assert.Equal(Status.Warning, result.Status);
        Assert.Equal(["no images found"], result.Messages);
        Assert.Equal(["pieceinfo.pdf"], OutNames());
    }

    [Fact]
    public async Task Pdf_AiExtension_Error_AsPdfOrPostScript()
    {
        var pdf = TestPdfs.OnePage(In, "logo.ai", Jpeg("l.jpg"), Cm(60, 100), Cm(40, 100));
        var postScript = Path.Combine(In, "old.ai");
        File.WriteAllText(postScript, "%!PS-Adobe-3.0 EPSF-3.0\n%%Creator: Adobe Illustrator\n%%Trailer\n");

        var fromPdf = await Process(pdf);
        var fromPostScript = await Process(postScript);

        Assert.Equal(["Illustrator files are not supported"], fromPdf.Messages);
        Assert.Equal(["Illustrator files are not supported"], fromPostScript.Messages);
        Assert.Empty(OutNames());
    }

    [Fact]
    public void IsPdf_FalseForImageSignatures()
    {
        Directory.CreateDirectory(_dir);
        string Write(string name, byte[] start)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, [.. start, .. "<x:xmpmeta>%PDF-1.4 in metadata</x:xmpmeta>"u8.ToArray()]);
            return path;
        }

        Assert.False(SourceImage.IsPdf(Write("a.jpg", [0xFF, 0xD8, 0xFF, 0xE1])));
        Assert.False(SourceImage.IsPdf(Write("a.tif", [(byte)'I', (byte)'I', (byte)'*', 0])));
        Assert.False(SourceImage.IsPdf(Write("b.tif", [(byte)'M', (byte)'M', 0, (byte)'*'])));
        Assert.False(SourceImage.IsPdf(Write("a.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])));
        Assert.True(SourceImage.IsPdf(Write("junk.bin", "junk\n"u8.ToArray())));
    }

    [Fact]
    public async Task Jpeg_WithPdfTextInItsMetadata_ProcessedAsImage()
    {
        var jpeg = Jpeg("meta.jpg");
        using (var image = new MagickImage(jpeg))
        {
            image.SetAttribute("comment", "%PDF-1.4 exported from somewhere");
            image.Write(jpeg);
        }
        Assert.Contains("%PDF-1.4", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(jpeg)));

        var result = await Process(jpeg);

        Assert.NotEqual(Status.Error, result.Status);
        Assert.DoesNotContain(result.Messages, m => m.StartsWith("page ", StringComparison.Ordinal));
        Assert.Equal(["meta.jpg"], OutNames());
    }

    [Fact]
    public void Translate_MapsFailuresToTheUsersMessages()
    {
        Assert.Equal("the PDF could not be read (cut short)",
            Assert.IsType<UnsupportedImageException>(PdfPipeline.Translate("page 1, image 1: ", new EndOfStreamException("cut short"))).Message);
        Assert.Equal("the PDF could not be read (bad data)",
            Assert.IsType<UnsupportedImageException>(PdfPipeline.Translate("page 1, image 1: ", new InvalidDataException("bad data"))).Message);
        Assert.Equal("page 1, image 1: the X colour space is not supported",
            PdfPipeline.Translate("page 1, image 1: ", new UnsupportedImageException("the X colour space is not supported"))!.Message);
        Assert.Null(PdfPipeline.Translate("page 1, image 1: ", new IOException("disk full")));
        Assert.Null(PdfPipeline.Translate("page 1, image 1: ", new OutOfMemoryException()));
    }

    [Fact]
    public void RawObjects_FileEndsWhileCopying_InvalidData()
    {
        var raw = new PdfRawObjects(new MemoryStream(new byte[10]), []);

        var ex = Assert.Throws<InvalidDataException>(() => raw.CopyTo(new MemoryStream(), 0, 20));

        Assert.Equal("the PDF file ended while an object was copied", ex.Message);
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

    private static PdfObjectId MaskId(string pdf, PdfObjectId image)
    {
        var reference = Assert.IsType<IndirectReferenceToken>(ImageStream(pdf, image).StreamDictionary.Data["SMask"]);
        return new PdfObjectId((int)reference.Data.ObjectNumber, reference.Data.Generation);
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new System.IO.Compression.ZLibStream(new MemoryStream(data), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Every stream that is not an image has the same raw (encoded) bytes in both files; returns how many.</summary>
    private static int AssertNonImageStreamsSame(string input, string output)
    {
        using var a = PdfDocument.Open(input);
        using var b = PdfDocument.Open(output);
        var count = 0;
        foreach (var reference in a.Structure.CrossReferenceTable.ObjectOffsets.Keys)
        {
            if (a.Structure.GetObject(reference).Data is not StreamToken stream) continue;
            if (stream.StreamDictionary.Data.TryGetValue("Subtype", out var subtype) && subtype is NameToken { Data: "Image" }) continue;
            if (stream.StreamDictionary.Data.TryGetValue("Type", out var type) && type is NameToken { Data: "ObjStm" or "XRef" }) continue;
            var after = Assert.IsType<StreamToken>(b.Structure.GetObject(reference).Data);
            Assert.Equal(stream.Data.ToArray(), after.Data.ToArray());
            count++;
        }
        return count;
    }
}
