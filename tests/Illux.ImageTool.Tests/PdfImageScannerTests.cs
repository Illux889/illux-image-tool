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
        foreach (var byteRange in new[] { false, true })
        {
            var pdf = TestPdfs.Signed(_dir, $"signed{byteRange}.pdf", byteRange);

            var ex = Assert.Throws<UnsupportedImageException>(() => PdfImageScanner.Scan(pdf));

            Assert.Equal("signed PDFs are not supported", ex.Message);
        }
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
}
