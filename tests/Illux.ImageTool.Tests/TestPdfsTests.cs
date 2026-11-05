using System.Text;
using ImageMagick;
using Illux.ImageTool.Jpeg;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.CrossReference;

namespace Illux.ImageTool.Tests;

public sealed class TestPdfsTests : IDisposable
{
    private const double PointsPerCm = 72 / 2.54;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-testpdfs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Jpeg(string name = "a.jpg") => TestImages.Write(_dir, name, 60, 45, ColorSpace.sRGB, 8, 72);

    private static string RawText(string pdf) => Encoding.Latin1.GetString(File.ReadAllBytes(pdf));

    /// <summary>Opens the PDF in PdfPig and checks the page sizes (cm, ±0.01 pt) and the image count of each page.</summary>
    private static void AssertPages(string pdf, params (double WidthCm, double HeightCm, int Images)[] expected)
    {
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        Assert.Equal(expected.Length, document.NumberOfPages);
        for (var i = 0; i < expected.Length; i++)
        {
            var page = document.GetPage(i + 1);
            Assert.Equal(expected[i].WidthCm * PointsPerCm, page.MediaBox.Bounds.Width, 0.01);
            Assert.Equal(expected[i].HeightCm * PointsPerCm, page.MediaBox.Bounds.Height, 0.01);
            Assert.Equal(expected[i].Images, page.GetImages().Count());
        }
    }

    /// <summary>The drawn lengths (cm) of the image's width axis and height axis, from PdfPig's four corners.</summary>
    private static (double Width, double Height) DrawnCm(IPdfImage image)
    {
        var b = image.BoundingBox;
        return (Distance(b.BottomLeft, b.BottomRight) / PointsPerCm, Distance(b.BottomLeft, b.TopLeft) / PointsPerCm);
    }

    private static double Distance(PdfPoint a, PdfPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    [Fact]
    public void OnePage_Jpeg_PageSizedToImage_EmbeddedAsDct()
    {
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);

        AssertPages(pdf, (10, 7.5, 1));
        Assert.StartsWith("%PDF-1.4\n", RawText(pdf));
        Assert.Contains("/Filter /DCTDecode", RawText(pdf));
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        var image = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(60, image.WidthInSamples);
        Assert.Equal(45, image.HeightInSamples);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "a.jpg")), image.RawBytes.ToArray());
    }

    [Fact]
    public void OnePage_PngWithAlpha_FlateWithSoftMask()
    {
        var png = TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 16, 72, alpha: true);

        var pdf = TestPdfs.OnePage(_dir, "alpha.pdf", png, 8, 6, version: "1.6");

        AssertPages(pdf, (8, 6, 1));
        var raw = RawText(pdf);
        Assert.StartsWith("%PDF-1.6\n", raw);
        Assert.Contains("/Filter /FlateDecode", raw);
        Assert.DoesNotContain("/DCTDecode", raw);
        Assert.Contains("/SMask", raw);
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        var image = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(16, image.BitsPerComponent);
        Assert.NotNull(image.MaskImage);
    }

    [Fact]
    public void OnePage_Rotated90_PageTurnedAndImageAxesKeepTheirLengths()
    {
        var pdf = TestPdfs.OnePage(_dir, "rot.pdf", Jpeg(), 10, 7.5, rotateDegrees: 90);

        AssertPages(pdf, (7.5, 10, 1));
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        var (w, h) = DrawnCm(Assert.Single(document.GetPage(1).GetImages()));
        Assert.Equal(10, w, 0.001);
        Assert.Equal(7.5, h, 0.001);
    }

    [Fact]
    public void Pages_OneImagePerPage_InOrder()
    {
        var pdf = TestPdfs.Pages(_dir, "pages.pdf", (Jpeg("1.jpg"), 10, 7.5), (Jpeg("2.jpg"), 20, 15), (Jpeg("3.jpg"), 4, 3));

        AssertPages(pdf, (10, 7.5, 1), (20, 15, 1), (4, 3, 1));
    }

    [Fact]
    public void InForm_ImageInsideFormWithMatrix_DrawnAtRequestedSize()
    {
        var pdf = TestPdfs.InForm(_dir, "form.pdf", Jpeg(), 10, 7.5);

        AssertPages(pdf, (10, 7.5, 1));
        var raw = RawText(pdf);
        Assert.Contains("/Subtype /Form", raw);
        Assert.Contains("/Matrix [2 0 0 1 0 0]", raw);
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        var (w, h) = DrawnCm(Assert.Single(document.GetPage(1).GetImages()));
        Assert.Equal(10, w, 0.001);
        Assert.Equal(7.5, h, 0.001);
    }

    [Fact]
    public void Reused_SameImageObjectOnTwoPages()
    {
        var pdf = TestPdfs.Reused(_dir, "reused.pdf", Jpeg(), (10, 7.5), (20, 15));

        AssertPages(pdf, (10, 7.5, 1), (20, 15, 1));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(RawText(pdf), "/Subtype /Image"));
    }

    [Fact]
    public void WithInlineImage_OneInlineImage()
    {
        var pdf = TestPdfs.WithInlineImage(_dir, "inline.pdf");

        AssertPages(pdf, (5, 5, 1));
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        Assert.True(Assert.Single(document.GetPage(1).GetImages()).IsInlineImage);
    }

    [Fact]
    public void NoImages_OnePageWithoutImages()
    {
        var pdf = TestPdfs.NoImages(_dir, "none.pdf");

        AssertPages(pdf, (21, 29.7, 0));
    }

    [Fact]
    public void Encrypted_TrailerHasEncrypt()
    {
        var pdf = TestPdfs.Encrypted(_dir, "enc.pdf");

        var raw = RawText(pdf);
        var trailer = raw[raw.LastIndexOf("trailer", StringComparison.Ordinal)..];
        Assert.Contains("/Encrypt", trailer);
    }

    [Fact]
    public void ChiliLike_IccJpegInFlippedClippedForm_DrawnAtRequestedSize()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg(), 10, 7.5);

        AssertPages(pdf, (10, 7.5, 1));
        var raw = RawText(pdf);
        Assert.StartsWith("%PDF-1.6\n", raw);
        Assert.Contains("/Subtype /Form", raw);
        Assert.Contains("/ColorSpace [/ICCBased", raw);
        Assert.Contains("/DecodeParms << >>", raw);
        Assert.Contains("/ExtGState << /GS1", raw);
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        var image = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "a.jpg")), image.RawBytes.ToArray());
        var (w, h) = DrawnCm(image);
        Assert.Equal(10, w, 0.001);
        Assert.Equal(7.5, h, 0.001);
    }

    [Fact]
    public void ChiliLike_CrLineEndings_AsChiliWritesThem()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili-cr.pdf", Jpeg(), 10, 7.5, crLineEndings: true);

        AssertPages(pdf, (10, 7.5, 1));
        var raw = RawText(pdf);
        Assert.StartsWith("%PDF-1.6\r%", raw);
        Assert.Contains(" 0 obj\r<<", raw);
        Assert.Contains(">>\rstream\n", raw); // "stream" itself must end in LF or CRLF (ISO 32000-1, 7.3.8.1)
        Assert.Contains("\rendstream\rendobj\r", raw);
        Assert.Contains("\rxref\r0 ", raw);
        Assert.Contains("0000000000 65535 f\r\n", raw); // table entries stay 20 bytes
        Assert.Contains(">>\rstartxref\r", raw);
        Assert.EndsWith("%%EOF\r", raw);
        // Outside stream data and the table entries no LF is left.
        var outsideData = string.Join("", raw.Split("stream\n").Select(part => part.Contains("endstream")
            ? part[part.IndexOf("endstream", StringComparison.Ordinal)..] : part));
        Assert.DoesNotContain("\n", outsideData.Replace(" f\r\n", "").Replace(" n\r\n", ""));
    }

    [Fact]
    public void WithXrefStream_Pdf15ObjectStreamAndXrefStream()
    {
        var pdf = TestPdfs.WithXrefStream(_dir, "xref.pdf", Jpeg(), 10, 7.5);

        AssertPages(pdf, (10, 7.5, 1));
        var raw = RawText(pdf);
        Assert.StartsWith("%PDF-1.5\n", raw);
        Assert.Contains("/Type /ObjStm", raw);
        Assert.Contains("/Type /XRef", raw);
        Assert.DoesNotContain("\nxref\n", raw);
        Assert.DoesNotContain("trailer", raw);
        using var document = PdfDocument.Open(File.ReadAllBytes(pdf));
        Assert.Equal(CrossReferenceType.Stream, Assert.Single(document.Structure.CrossReferenceTable.Parts).Type);
        var (w, h) = DrawnCm(Assert.Single(document.GetPage(1).GetImages()));
        Assert.Equal(10, w, 0.001);
        Assert.Equal(7.5, h, 0.001);
    }

    [Fact]
    public void OnePage_16BitPng_RaisesVersionTo15()
    {
        var png = TestImages.Write(_dir, "b.png", 40, 30, ColorSpace.sRGB, 16, 72);

        Assert.StartsWith("%PDF-1.5\n", RawText(TestPdfs.OnePage(_dir, "v.pdf", png, 8, 6)));
        Assert.StartsWith("%PDF-1.4\n", RawText(TestPdfs.OnePage(_dir, "w.pdf", Jpeg(), 8, 6)));
    }

    [Fact]
    public void ChiliLike_AdobeCmykJpeg_HasInvertedDecode()
    {
        var cmyk = TestImages.Write(_dir, "c.jpg", 60, 45, ColorSpace.CMYK, 8, 72);
        Assert.True(JpegInspector.Inspect(cmyk).HasAdobeMarker);

        var pdf = TestPdfs.ChiliLike(_dir, "chili-cmyk.pdf", cmyk, 10, 7.5);

        Assert.Contains("/DCTDecode /Decode [1 0 1 0 1 0 1 0] /DecodeParms", RawText(pdf));
    }

    [Fact]
    public void Signed_HasSigFlagsOrByteRange()
    {
        Assert.Contains("/SigFlags 3", RawText(TestPdfs.Signed(_dir, "s1.pdf")));
        Assert.Contains("/ByteRange", RawText(TestPdfs.Signed(_dir, "s2.pdf", byteRange: true)));
    }
}
