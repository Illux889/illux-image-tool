using ImageMagick;
using Illux.ImageTool.Imaging;
using Illux.ImageTool.Pdf.Input;

namespace Illux.ImageTool.Tests;

public sealed class PdfImageScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdfscan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Jpeg(string name = "a.jpg", int w = 60, int h = 45) => TestImages.Write(_dir, name, w, h, ColorSpace.sRGB, 8, 72);

    [Fact]
    public void Scan_OnePage_SizeInCm()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 20, 10));

        Assert.Equal("1.4", scan.Version);
        Assert.Equal(1, scan.PageCount);
        Assert.Equal(0, scan.InlineImages);
        var image = Assert.Single(scan.Images);
        Assert.Equal(1, image.FirstPage);
        Assert.Equal(1, image.FirstIndex);
        Assert.False(image.DifferentSizes);
        Assert.Equal(20, image.WidthCm, 0.01);
        Assert.Equal(10, image.HeightCm, 0.01);
    }

    [Fact]
    public void Scan_Rotated90_UsesImageAxes()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.OnePage(_dir, "rot.pdf", Jpeg("r.jpg", 800, 400), 20, 10, rotateDegrees: 90));

        var image = Assert.Single(scan.Images);
        Assert.Equal(20, image.WidthCm, 0.01);
        Assert.Equal(10, image.HeightCm, 0.01);
    }

    [Fact]
    public void Scan_Pages_InOrder()
    {
        var pdf = TestPdfs.Pages(_dir, "pages.pdf", (Jpeg("1.jpg"), 10, 7.5), (Jpeg("2.jpg"), 20, 15), (Jpeg("3.jpg"), 4, 3));

        var scan = PdfImageScanner.Scan(pdf);

        Assert.Equal(3, scan.PageCount);
        Assert.Equal([1, 2, 3], scan.Images.Select(i => i.FirstPage));
        Assert.All(scan.Images, i => Assert.Equal(1, i.FirstIndex));
        Assert.Equal([10.0, 20.0, 4.0], scan.Images.Select(i => Math.Round(i.WidthCm, 2)));
        Assert.Equal(3, scan.Images.Select(i => i.Image).Distinct().Count());
    }

    [Fact]
    public void Scan_InForm_AppliesFormMatrix()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.InForm(_dir, "form.pdf", Jpeg(), 10, 7.5));

        var image = Assert.Single(scan.Images);
        Assert.Equal(10, image.WidthCm, 0.01);
        Assert.Equal(7.5, image.HeightCm, 0.01);
    }

    [Fact]
    public void Scan_ReusedAtTwoSizes_LargestAndFlag()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.Reused(_dir, "reused.pdf", Jpeg(), (10, 7.5), (20, 15)));

        var image = Assert.Single(scan.Images);
        Assert.True(image.DifferentSizes);
        Assert.Equal(20, image.WidthCm, 0.01);
        Assert.Equal(15, image.HeightCm, 0.01);
        Assert.Equal(1, image.FirstPage);
    }

    [Fact]
    public void Scan_ReusedAtSameSize_NoFlag()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.Reused(_dir, "same.pdf", Jpeg(), (10, 7.5), (10, 7.5)));

        Assert.False(Assert.Single(scan.Images).DifferentSizes);
    }

    [Fact]
    public void Scan_InlineImage_Counted()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithInlineImage(_dir, "inline.pdf"));

        Assert.Equal(1, scan.InlineImages);
        Assert.Empty(scan.Images);
    }

    [Fact]
    public void Scan_NoImages_Empty()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.NoImages(_dir, "none.pdf"));

        Assert.Equal(1, scan.PageCount);
        Assert.Empty(scan.Images);
        Assert.Equal(0, scan.InlineImages);
    }

    [Fact]
    public void Scan_Encrypted_Throws()
    {
        var ex = Assert.Throws<UnsupportedImageException>(() => PdfImageScanner.Scan(TestPdfs.Encrypted(_dir, "enc.pdf")));

        Assert.Equal("encrypted PDFs are not supported", ex.Message);
    }

    [Fact]
    public void Scan_Garbage_ThrowsUnreadable()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "garbage.pdf");
        File.WriteAllText(path, "this is not a pdf at all");

        var ex = Assert.Throws<UnsupportedImageException>(() => PdfImageScanner.Scan(path));

        Assert.StartsWith("the PDF could not be read (", ex.Message);
        Assert.EndsWith(")", ex.Message);
    }

    [Fact]
    public void Scan_XrefStream_Works()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithXrefStream(_dir, "xref.pdf", Jpeg(), 10, 7.5));

        Assert.Equal("1.5", scan.Version);
        var image = Assert.Single(scan.Images);
        Assert.Equal(10, image.WidthCm, 0.01);
        Assert.Equal(7.5, image.HeightCm, 0.01);
    }

    [Fact]
    public void Scan_SoftMask_NotListedAsOwnImage()
    {
        var png = TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 8, 72, alpha: true);

        var scan = PdfImageScanner.Scan(TestPdfs.OnePage(_dir, "alpha.pdf", png, 8, 6));

        Assert.Single(scan.Images);
    }

    [Fact]
    public void Scan_ChiliLike_DrawnSizeMatches()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg(), 10, 7.5));

        Assert.Equal("1.6", scan.Version);
        var image = Assert.Single(scan.Images);
        Assert.Equal(10, image.WidthCm, 0.01);
        Assert.Equal(7.5, image.HeightCm, 0.01);
        Assert.False(image.DifferentSizes);
    }

    [Fact]
    public void Scan_Signed_Throws()
    {
        // Ruling Q6a: a signature dictionary with /ByteRange.
        var pdf = TestPdfs.Signed(_dir, "signed.pdf", byteRange: true);

        var ex = Assert.Throws<UnsupportedImageException>(() => PdfImageScanner.Scan(pdf));

        Assert.Equal("signed PDFs are not supported", ex.Message);
    }

    [Fact]
    public void Scan_BlankSignatureFieldWithSigFlags_Scanned()
    {
        // Ruling Q6a: /SigFlags alone (an unsigned signature field) no longer refuses the file.
        var scan = PdfImageScanner.Scan(TestPdfs.Signed(_dir, "blank.pdf"));

        Assert.Equal(1, scan.PageCount);
        Assert.Empty(scan.Images);
    }

    // ---- final fix wave: images the walk does not reach (Q16), per-axis maximum (Q17), form fan-out ----

    [Theory]
    [InlineData("annotation")]
    [InlineData("pattern")]
    public void Scan_ImageNotDrawnOnAPage_Counted(string where)
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithUndrawnImage(_dir, where + ".pdf", Jpeg(), where));

        Assert.Empty(scan.Images);
        Assert.Equal(1, scan.NotDrawnImages);
    }

    [Fact]
    public void Scan_PageThumbnail_NotCounted()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithUndrawnImage(_dir, "thumb.pdf", Jpeg(), "thumb"));

        Assert.Single(scan.Images);
        Assert.Equal(0, scan.NotDrawnImages);
    }

    [Fact]
    public void Scan_SoftMaskAndInvisibleUse_NotCounted()
    {
        // An image's /SMask is not drawn on its own; an image drawn only with a degenerate cm was reached by the walk.
        var png = TestImages.Write(_dir, "m.png", 40, 30, ColorSpace.sRGB, 8, 72, alpha: true);

        Assert.Equal(0, PdfImageScanner.Scan(TestPdfs.OnePage(_dir, "masked.pdf", png, 8, 6)).NotDrawnImages);
        Assert.Equal(0, PdfImageScanner.Scan(TestPdfs.WithDegenerateUse(_dir, "deg3.pdf", Jpeg(), alsoVisible: false)).NotDrawnImages);
    }

    [Fact]
    public void Scan_StencilMaskOfADrawnImage_NotCounted()
    {
        // A stencil /Mask (an image stream of its own) of a drawn image.
        var pdf = TestPdfs.CustomImage(_dir, "stencilmask.pdf", w =>
        {
            var mask = TestPdfs.WriteStream(w, "/Type /XObject /Subtype /Image /Width 8 /Height 2 /ImageMask true /BitsPerComponent 1",
                [0b1010_1010, 0b0101_0101]);
            return ($"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask {mask} 0 R",
                [0, 80, 160, 255]);
        });

        var scan = PdfImageScanner.Scan(pdf);

        Assert.Single(scan.Images);
        Assert.Equal(0, scan.NotDrawnImages);
    }

    [Fact]
    public void Scan_ReusedAtCrossedSizes_PerAxisMaximum()
    {
        // Ruling Q17: 10 × 30 cm and 20 × 10 cm give 20 × 30 cm, so the image is never below the target anywhere.
        var scan = PdfImageScanner.Scan(TestPdfs.Reused(_dir, "crossed.pdf", Jpeg(), (10, 30), (20, 10)));

        var image = Assert.Single(scan.Images);
        Assert.True(image.DifferentSizes);
        Assert.Equal(20, image.WidthCm, 0.01);
        Assert.Equal(30, image.HeightCm, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task Scan_FormFanOut_FailsCleanlyAndFast()
    {
        // Forms 8 deep, each drawing the next 10 times: 10^7 image draws. The walk stops at 100,000 Do expansions.
        var pdf = TestPdfs.NestedForms(_dir, "fanout.pdf", Jpeg(), depth: 8, fanOut: 10);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<UnsupportedImageException>(() => Task.Run(() => PdfImageScanner.Scan(pdf)));

        Assert.Equal("the PDF could not be read (too many nested drawings)", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Scan_FormFanOutBelowTheCap_OneImage()
    {
        // 4 deep × 10: 1,000 draws of one image (1,111 Do expansions).
        var scan = PdfImageScanner.Scan(TestPdfs.NestedForms(_dir, "fanout-small.pdf", Jpeg(), depth: 4, fanOut: 10));

        var image = Assert.Single(scan.Images);
        Assert.False(image.DifferentSizes);
        Assert.Equal(5, image.WidthCm, 0.01);
    }

    [Fact]
    public void Scan_InlineImageInAFormDrawnFiveTimes_CountedOnce()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.InlineImageInForm(_dir, "inline-form.pdf", draws: 5));

        Assert.Equal(1, scan.InlineImages);
    }

    [Fact]
    public void Scan_SignatureInObjectStream_Throws()
    {
        var pdf = TestPdfs.WithXrefStream(_dir, "signedxref.pdf", Jpeg(), 10, 7.5, signed: true);

        var ex = Assert.Throws<UnsupportedImageException>(() => PdfImageScanner.Scan(pdf));

        Assert.Equal("signed PDFs are not supported", ex.Message);
    }

    [Fact]
    public void Scan_UnparseableUnreferencedObject_StillScans()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithBrokenObject(_dir, "broken.pdf", Jpeg()));

        Assert.Single(scan.Images);
    }

    [Fact]
    public void Scan_Version20Header_StaysTwoPointZero()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.OnePage(_dir, "v20.pdf", Jpeg(), 10, 7.5, version: "2.0"));

        Assert.Equal("2.0", scan.Version);
    }

    [Fact]
    public void Scan_OnlyDegenerateUse_NotListed()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithDegenerateUse(_dir, "deg.pdf", Jpeg(), alsoVisible: false));

        Assert.Empty(scan.Images);
    }

    [Fact]
    public void Scan_DegenerateAndVisibleUse_VisibleSizeOnly()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithDegenerateUse(_dir, "deg2.pdf", Jpeg(), alsoVisible: true));

        var image = Assert.Single(scan.Images);
        Assert.False(image.DifferentSizes);
        Assert.Equal(5, image.WidthCm, 0.01);
    }

    [Fact]
    public void Scan_CatalogVersionHigher_Wins()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithCatalogVersion(_dir, "catver.pdf", Jpeg(), "1.4", "1.7"));

        Assert.Equal("1.7", scan.Version);
    }

    [Fact]
    public void Scan_CatalogVersionLower_HeaderWins()
    {
        var scan = PdfImageScanner.Scan(TestPdfs.WithCatalogVersion(_dir, "catver-low.pdf", Jpeg(), "1.6", "1.3"));

        Assert.Equal("1.6", scan.Version);
    }

    [Fact]
    public void FirstLine_KeepsOnlyTheFirstNonEmptyLine()
    {
        Assert.Equal("first line", PdfImageScanner.FirstLine("\r\n  first line  \r\nsecond line\nthird"));
        Assert.Equal("unknown error", PdfImageScanner.FirstLine("\r\n"));
    }

    [Fact]
    public void Scan_NotANumberAxis_NotListed()
    {
        // 1e200 squared overflows to infinity; the swap matrix then multiplies infinity by 0: NaN axis lengths.
        var big = "1" + new string('0', 200);
        var matrices = $"{big} 0 0 {big} 0 0 cm {big} 0 0 {big} 0 0 cm 0 1 1 0 0 0 cm";

        var scan = PdfImageScanner.Scan(TestPdfs.WithMatrices(_dir, "nan.pdf", Jpeg(), matrices));

        Assert.Empty(scan.Images);
    }

    [Fact]
    public void Scan_InfiniteAxis_NotListed()
    {
        // 1e200 squared overflows to infinity: both axes are infinitely long.
        var big = "1" + new string('0', 200);

        var scan = PdfImageScanner.Scan(TestPdfs.WithMatrices(_dir, "inf.pdf", Jpeg(), $"{big} 0 0 {big} 0 0 cm {big} 0 0 {big} 0 0 cm"));

        Assert.Empty(scan.Images);
    }
}
