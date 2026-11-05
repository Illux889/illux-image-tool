using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Illux.ImageTool.Options;
using Illux.ImageTool.Pdf.Input;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Tests;

/// <summary>
/// <see cref="PdfRewriter"/>: a full rewrite of the input (decision P1) in which only the given objects change. Objects
/// are compared through PdfPig: dictionaries and arrays entry for entry, strings by their bytes, stream data byte for
/// byte; a stream's <c>/Length</c> is checked separately (it becomes direct).
/// </summary>
public sealed class PdfRewriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdfrewrite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Out(string name = "out.pdf") => Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName, name);

    private string Jpeg(string name = "a.jpg", int w = 60, int h = 45) => TestImages.Write(_dir, name, w, h, ColorSpace.sRGB, 8, 72);

    private static readonly Dictionary<PdfObjectId, PdfImageStream> None = [];

    // ---- the brief's tests ----

    [Fact]
    public void Rewrite_ReplacesOnlyGivenObjects()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg(), 10, 7.5);
        var id = ImageId(pdf);
        var newJpeg = File.ReadAllBytes(Jpeg("new.jpg", 120, 90));
        var replacement = JpegStream(newJpeg, 120, 90, "/Length 7");

        PdfRewriter.Write(pdf, Out(), new Dictionary<PdfObjectId, PdfImageStream> { [id] = replacement });

        var before = Objects(pdf);
        var after = Objects(Out());
        Assert.Equal(before.Keys.OrderBy(Key), after.Keys.OrderBy(Key));
        foreach (var (reference, token) in before)
        {
            if (reference.ObjectNumber == id.Number) continue;
            AssertSame(token, after[reference], reference.ToString());
        }
        var image = Assert.IsType<StreamToken>(after[new IndirectReference(id.Number, id.Generation)]);
        Assert.Equal(newJpeg, image.Data.ToArray());
        Assert.Equal(120, Int(image.StreamDictionary, "Width"));
        Assert.Equal(90, Int(image.StreamDictionary, "Height"));
        Assert.Equal(newJpeg.Length, Int(image.StreamDictionary, "Length"));
        Assert.Equal(["BitsPerComponent", "ColorSpace", "Filter", "Height", "Length", "Subtype", "Type", "Width"],
            image.StreamDictionary.Data.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Rewrite_NoReplacements_EveryObjectSame()
    {
        var pdf = TestPdfs.InForm(_dir, "form.pdf", Jpeg(), 10, 7.5);

        PdfRewriter.Write(pdf, Out(), None);

        var before = Objects(pdf);
        var after = Objects(Out());
        Assert.Equal(before.Keys.OrderBy(Key), after.Keys.OrderBy(Key));
        foreach (var (reference, token) in before) AssertSame(token, after[reference], reference.ToString());
        AssertValidStructure(Out());
    }

    [Fact]
    public void Rewrite_PageSizesAndContentUnchanged()
    {
        var pdf = TestPdfs.Pages(_dir, "pages.pdf", (Jpeg("p1.jpg"), 10, 7.5), (Jpeg("p2.jpg", 40, 80), 5, 10));
        var form = TestPdfs.InForm(_dir, "form.pdf", Jpeg("f.jpg"), 12, 9);
        var chili = TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg("c.jpg"), 10, 7.5, crLineEndings: true);

        foreach (var input in new[] { pdf, form, chili })
        {
            var output = Out(Path.GetFileName(input));
            var scan = PdfImageScanner.Scan(input);
            var replacements = scan.Images.ToDictionary(i => i.Image, _ => JpegStream(File.ReadAllBytes(Jpeg("n.jpg", 30, 20)), 30, 20));

            PdfRewriter.Write(input, output, replacements);

            using (var a = PdfDocument.Open(input))
            using (var b = PdfDocument.Open(output))
            {
                Assert.Equal(a.NumberOfPages, b.NumberOfPages);
                for (var n = 1; n <= a.NumberOfPages; n++)
                {
                    Assert.Equal(a.GetPage(n).MediaBox.Bounds, b.GetPage(n).MediaBox.Bounds);
                    Assert.Equal(a.GetPage(n).CropBox.Bounds, b.GetPage(n).CropBox.Bounds);
                }
                // Every stream that is not an image (page content, forms, ICC profiles): decoded bytes identical.
                var decoded = 0;
                foreach (var reference in a.Structure.CrossReferenceTable.ObjectOffsets.Keys)
                {
                    if (a.Structure.GetObject(reference).Data is not StreamToken s || IsImage(s)) continue;
                    var t = Assert.IsType<StreamToken>(b.Structure.GetObject(reference).Data);
                    Assert.Equal(s.Decode(a.Structure.FilterProvider, a.Structure.TokenScanner).ToArray(),
                        t.Decode(b.Structure.FilterProvider, b.Structure.TokenScanner).ToArray());
                    decoded++;
                }
                Assert.True(decoded >= a.NumberOfPages);
            }

            var rescan = PdfImageScanner.Scan(output);
            Assert.Equal(scan.PageCount, rescan.PageCount);
            Assert.Equal(scan.Images.Count, rescan.Images.Count);
            foreach (var (x, y) in scan.Images.Zip(rescan.Images))
            {
                Assert.Equal(x.Image, y.Image);
                Assert.Equal(x.WidthCm, y.WidthCm, 1e-6);
                Assert.Equal(x.HeightCm, y.HeightCm, 1e-6);
            }
        }
    }

    [Fact]
    public void Rewrite_VersionNotLowered()
    {
        var v17 = TestPdfs.OnePage(_dir, "v17.pdf", Jpeg(), 10, 7.5, version: "1.7");
        var v20 = TestPdfs.OnePage(_dir, "v20.pdf", Jpeg(), 10, 7.5, version: "2.0");
        var catalog = TestPdfs.WithCatalogVersion(_dir, "cat.pdf", Jpeg(), "1.4", "1.7");

        PdfRewriter.Write(v17, Out("v17.pdf"), None);
        PdfRewriter.Write(v20, Out("v20.pdf"), None);
        PdfRewriter.Write(catalog, Out("cat.pdf"), None);

        Assert.StartsWith("%PDF-1.7\n", RawText(Out("v17.pdf")));
        Assert.Equal("1.7", PdfImageScanner.Scan(Out("v17.pdf")).Version);
        Assert.StartsWith("%PDF-2.0\n", RawText(Out("v20.pdf")));
        Assert.Equal("2.0", PdfImageScanner.Scan(Out("v20.pdf")).Version);
        // Header as in the input; the catalog's /Version is copied untouched.
        Assert.StartsWith("%PDF-1.4\n", RawText(Out("cat.pdf")));
        Assert.Equal("1.7", PdfImageScanner.Scan(Out("cat.pdf")).Version);
    }

    [Fact]
    public void Rewrite_XrefStreamInput_Works()
    {
        var pdf = TestPdfs.WithXrefStream(_dir, "xref.pdf", Jpeg(), 10, 7.5);
        var id = ImageId(pdf);
        var newJpeg = File.ReadAllBytes(Jpeg("new.jpg", 30, 20));

        PdfRewriter.Write(pdf, Out(), new Dictionary<PdfObjectId, PdfImageStream> { [id] = JpegStream(newJpeg, 30, 20) });

        var raw = RawText(Out());
        Assert.StartsWith("%PDF-1.5\n", raw);
        Assert.DoesNotContain("/ObjStm", raw);
        Assert.DoesNotContain("/XRef", raw);
        Assert.DoesNotContain("/W [", raw);
        AssertValidStructure(Out());

        // Every object but the object stream and the xref stream is there, as a plain object.
        var before = Objects(pdf).Where(o => !IsStructural(o.Value)).ToDictionary();
        var after = Objects(Out());
        Assert.Equal(before.Keys.OrderBy(Key), after.Keys.OrderBy(Key));
        foreach (var (reference, token) in before)
            if (reference.ObjectNumber != id.Number) AssertSame(token, after[reference], reference.ToString());
        using var document = PdfDocument.Open(Out());
        Assert.Equal(newJpeg, Assert.Single(document.GetPage(1).GetImages()).RawBytes.ToArray());
        var image = Assert.Single(PdfImageScanner.Scan(Out()).Images);
        Assert.Equal(10, image.WidthCm, 0.001);
        Assert.Equal(7.5, image.HeightCm, 0.001);
    }

    [Fact]
    public void Rewrite_Failure_LeavesNoFile()
    {
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        var id = ImageId(pdf);
        var jpeg = File.ReadAllBytes(Jpeg("n.jpg", 30, 20));
        var good = JpegStream(jpeg, 30, 20);

        void Fails<T>(string input, IReadOnlyDictionary<PdfObjectId, PdfImageStream> replacements) where T : Exception
        {
            var output = Out();
            Assert.ThrowsAny<T>(() => PdfRewriter.Write(input, output, replacements));
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!));
        }

        // An object the file does not have (number or generation).
        Fails<ArgumentException>(pdf, new Dictionary<PdfObjectId, PdfImageStream> { [new PdfObjectId(999, 0)] = good });
        Fails<ArgumentException>(pdf, new Dictionary<PdfObjectId, PdfImageStream> { [id with { Generation = 1 }] = good });
        // A replacement dictionary that is not exactly one dictionary.
        Fails<Exception>(pdf, new Dictionary<PdfObjectId, PdfImageStream> { [id] = new("[1 2]"u8.ToArray(), jpeg) });
        Fails<Exception>(pdf, new Dictionary<PdfObjectId, PdfImageStream> { [id] = new("<< /A 1 >> << /B 2 >>"u8.ToArray(), jpeg) });
        Fails<Exception>(pdf, new Dictionary<PdfObjectId, PdfImageStream> { [id] = new("<< /A [1 2 >>"u8.ToArray(), jpeg) });
        // An input that cannot be read, or that is encrypted.
        Fails<Exception>(Path.Combine(_dir, "missing.pdf"), None);
        File.WriteAllText(Path.Combine(_dir, "junk.pdf"), "not a PDF at all");
        Fails<Exception>(Path.Combine(_dir, "junk.pdf"), None);
        Fails<Exception>(TestPdfs.Encrypted(_dir, "enc.pdf"), None);
    }

    [Fact]
    public void Rewrite_FailureAfterWritingStarted_RemovesPartialFile()
    {
        // The name is only found ambiguous while the objects are written, after the output file was created.
        var pdf = TestPdfs.CustomImage(_dir, "name.pdf", w =>
        {
            TestPdfs.WriteStream(w, "/Filler true", new byte[100_000]);
            w.WriteObject(w.Reserve(), "<< /caf#E9 1 >>");
            return GrayImage();
        });

        Assert.ThrowsAny<Exception>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.False(File.Exists(Out()));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Out())!));
    }

    [Fact]
    public void Rewrite_Failure_RemovesAnOlderFileAtOutput()
    {
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        var jpeg = File.ReadAllBytes(Jpeg("n.jpg", 30, 20));
        File.WriteAllText(Out(), "an older output");

        Assert.ThrowsAny<ArgumentException>(() => PdfRewriter.Write(pdf, Out(),
            new Dictionary<PdfObjectId, PdfImageStream> { [new PdfObjectId(999, 0)] = JpegStream(jpeg, 30, 20) }));

        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public void Rewrite_SamePathForInputAndOutput_Throws()
    {
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        var bytes = File.ReadAllBytes(pdf);

        Assert.Throws<ArgumentException>(() => PdfRewriter.Write(pdf, pdf, None));

        Assert.Equal(bytes, File.ReadAllBytes(pdf));
    }

    [Fact]
    public void Rewrite_OutputIsValidPdf()
    {
        foreach (var input in new[]
                 {
                     TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5),
                     TestPdfs.Reused(_dir, "reused.pdf", Jpeg(), (10, 7.5), (20, 15)),
                     TestPdfs.OnePage(_dir, "alpha.pdf", TestImages.Write(_dir, "a.png", 40, 30, ColorSpace.sRGB, 16, 72, alpha: true), 8, 6),
                     TestPdfs.ChiliLike(_dir, "chili-cr.pdf", Jpeg(), 10, 7.5, crLineEndings: true),
                     TestPdfs.WithXrefStream(_dir, "xref.pdf", Jpeg(), 10, 7.5),
                 })
        {
            var output = Out(Path.GetFileName(input));
            PdfRewriter.Write(input, output, None);

            var bytes = File.ReadAllBytes(output);
            Assert.True(bytes.AsSpan().StartsWith("%PDF-1."u8));
            Assert.Equal((byte)'\n', bytes[8]);
            Assert.Equal((byte)'%', bytes[9]);
            Assert.True(bytes.Skip(10).Take(4).All(b => b >= 128), "the second line is a comment of four high bytes");
            Assert.True(bytes.AsSpan().EndsWith("%%EOF\n"u8));
            AssertValidStructure(output);
            using var document = PdfDocument.Open(output);
            Assert.True(document.NumberOfPages >= 1);
            foreach (var page in document.GetPages()) Assert.NotEmpty(page.GetImages());
        }
    }

    // ---- further behaviour ----

    [Fact]
    public void Rewrite_ChiliLikeWithCrLineEndings_ReplacesImage()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili-cr.pdf", Jpeg(), 10, 7.5, crLineEndings: true);
        Assert.StartsWith("%PDF-1.6\r", RawText(pdf));
        var id = ImageId(pdf);
        var newJpeg = File.ReadAllBytes(Jpeg("new.jpg", 120, 90));

        PdfRewriter.Write(pdf, Out(), new Dictionary<PdfObjectId, PdfImageStream> { [id] = JpegStream(newJpeg, 120, 90) });

        AssertValidStructure(Out());
        Assert.StartsWith("%PDF-1.6\n", RawText(Out()));
        var before = Objects(pdf);
        var after = Objects(Out());
        foreach (var (reference, token) in before)
            if (reference.ObjectNumber != id.Number) AssertSame(token, after[reference], reference.ToString());
        using var document = PdfDocument.Open(Out());
        var image = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(120, image.WidthInSamples);
        Assert.Equal(newJpeg, image.RawBytes.ToArray());
        var drawn = Assert.Single(PdfImageScanner.Scan(Out()).Images);
        Assert.Equal(10, drawn.WidthCm, 0.001);
        Assert.Equal(7.5, drawn.HeightCm, 0.001);
    }

    [Fact]
    public void Rewrite_EncoderOutput_NewSizeSameDrawnSize()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg("big.jpg", 200, 150), 10, 7.5, crLineEndings: true);
        var id = ImageId(pdf);
        var work = Path.Combine(_dir, "work");
        PdfImageStream replacement;
        using (var doc = PdfDocument.Open(pdf))
        {
            var extracted = PdfImageExtractor.Extract(doc, id, needsScaling: true, work);
            var scaled = Path.Combine(work, "scaled.jpg");
            using (var image = new MagickImage(extracted.Path))
            {
                image.Resize(new MagickGeometry(100, 75) { IgnoreAspectRatio = true });
                image.Write(scaled, MagickFormat.Jpeg);
            }
            replacement = PdfImageEncoder.Encode(doc, id, scaled, extracted, (0.5, 0.5), ResampleFilter.Bicubic).Image;
        }

        PdfRewriter.Write(pdf, Out(), new Dictionary<PdfObjectId, PdfImageStream> { [id] = replacement });

        AssertValidStructure(Out());
        using var document = PdfDocument.Open(Out());
        var drawnImage = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal((100, 75), (drawnImage.WidthInSamples, drawnImage.HeightInSamples));
        var drawn = Assert.Single(PdfImageScanner.Scan(Out()).Images);
        Assert.Equal(10, drawn.WidthCm, 0.001);
        Assert.Equal(7.5, drawn.HeightCm, 0.001);
        Assert.True(new FileInfo(Out()).Length < new FileInfo(pdf).Length, "the old image data is gone");
    }

    [Fact]
    public void Rewrite_ReplacementLength_IsTheDataLength()
    {
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        var id = ImageId(pdf);
        var jpeg = File.ReadAllBytes(Jpeg("n.jpg", 30, 20));

        foreach (var length in new[] { "", "/Length 7", "/Length 3 0 R", "/Length -1" })
        {
            var output = Out($"len{length.Length}.pdf");
            PdfRewriter.Write(pdf, output, new Dictionary<PdfObjectId, PdfImageStream> { [id] = JpegStream(jpeg, 30, 20, length) });

            using var document = PdfDocument.Open(output);
            var image = Assert.IsType<StreamToken>(document.Structure.GetObject(new IndirectReference(id.Number, id.Generation)).Data);
            Assert.Equal(jpeg.Length, Assert.IsType<NumericToken>(image.StreamDictionary.Data["Length"]).Int);
            Assert.Equal(jpeg, image.Data.ToArray());
        }
    }

    [Fact]
    public void Rewrite_NumbersStringsAndNames_KeptExactly()
    {
        const string Extra =
            @"<< /Big 3000000000 /Neg -2147483649 /Real 1.00135 /Tiny 0.000001 /NegReal -.5 /Huge 123456789012.25 " +
            @"/Doc (\200\201\240\351 caf\351) /Bom (\376\377\000) /Esc (a\(b\)c\\d\r\n\t\000) /Hex <00FF10> /Utf16 <FEFF20AC> " +
            @"/Name /A#23B#20C#2F /Euro /#E2#82#AC /Mojibake /#C3#83#C2#A9 /Arr [1 0 R null true false [ ] << >>] /Null null >>";
        var pdf = TestPdfs.CustomImage(_dir, "tokens.pdf", w =>
        {
            w.WriteObject(w.Reserve(), Extra);
            return GrayImage();
        });

        PdfRewriter.Write(pdf, Out(), None);

        var before = Objects(pdf);
        var after = Objects(Out());
        var reference = before.Single(o => o.Value is DictionaryToken d && d.Data.ContainsKey("Big")).Key;
        AssertSame(before[reference], after[reference], "the token object");
        var d = Assert.IsType<DictionaryToken>(after[reference]);
        Assert.Equal(3000000000L, Assert.IsType<NumericToken>(d.Data["Big"]).Long);
        Assert.Equal(-2147483649L, Assert.IsType<NumericToken>(d.Data["Neg"]).Long);
        Assert.Equal(1.00135, Assert.IsType<NumericToken>(d.Data["Real"]).Double);
        Assert.Equal(0.000001, Assert.IsType<NumericToken>(d.Data["Tiny"]).Double);
        Assert.Equal([0x80, 0x81, 0xA0, 0xE9, 0x20, 0x63, 0x61, 0x66, 0xE9], Assert.IsType<StringToken>(d.Data["Doc"]).GetBytes());
        Assert.Equal([0xFE, 0xFF, 0x00], Assert.IsType<StringToken>(d.Data["Bom"]).GetBytes());
        Assert.Equal("a(b)c\\d\r\n\t\0"u8.ToArray(), Assert.IsType<StringToken>(d.Data["Esc"]).GetBytes());
        Assert.Equal("A#B C/", Assert.IsType<NameToken>(d.Data["Name"]).Data);
        Assert.Equal("€", Assert.IsType<NameToken>(d.Data["Euro"]).Data);
        var raw = RawText(Out());
        Assert.Contains("3000000000", raw);
        Assert.Contains("-2147483649", raw);
        Assert.Contains("0.000001", raw);
        Assert.Contains("/A#23B#20C#2F", raw);
        Assert.Contains("/#E2#82#AC", raw);
        Assert.Contains("/#C3#83#C2#A9", raw); // "Ã©" from UTF-8: as Latin-1 its bytes would read as UTF-8 "é"
        Assert.DoesNotMatch(new Regex(@"\d[eE][+-]?\d"), Regex.Replace(raw, @"stream\n.*?endstream", "", RegexOptions.Singleline));
    }

    [Fact]
    public void Rewrite_NonAsciiNameOfUnknownEncoding_Throws()
    {
        // PdfPig reads both /caf#E9 (Latin-1) and /caf#C3#A9 (UTF-8) as "café"; which bytes were in the file is lost.
        var pdf = TestPdfs.CustomImage(_dir, "name.pdf", w =>
        {
            w.WriteObject(w.Reserve(), "<< /caf#C3#A9 1 >>");
            return GrayImage();
        });

        Assert.ThrowsAny<NotSupportedException>(() => PdfRewriter.Write(pdf, Out(), None));
        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public void Rewrite_UnreadableObject_Throws_LeavesNoFile()
    {
        // An object PdfPig cannot read cannot be copied; the rewrite does not guess (the scan still accepts the file).
        var pdf = TestPdfs.WithBrokenObject(_dir, "broken.pdf", Jpeg());

        var ex = Assert.Throws<InvalidDataException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.Matches(@"^object \d+ 0 R could not be read", ex.Message);
        Assert.False(File.Exists(Out()));
    }

    [Fact]
    public void Rewrite_LinearizationDictionary_Dropped()
    {
        var number = 0;
        var pdf = TestPdfs.CustomImage(_dir, "lin.pdf", w =>
        {
            number = w.Reserve();
            w.WriteObject(number, "<< /Linearized 1 /L 1000 /H [10 20] /O 3 /E 500 /N 1 /T 900 >>");
            return GrayImage();
        });

        PdfRewriter.Write(pdf, Out(), None);

        Assert.DoesNotContain("/Linearized", RawText(Out()));
        AssertValidStructure(Out());
        var entries = XrefEntries(File.ReadAllBytes(Out()));
        Assert.Equal('f', entries[number].Type);
        Assert.Equal(Objects(pdf).Count - 1, Objects(Out()).Count);
    }

    [Fact]
    public void Rewrite_Trailer_KeepsRootInfoAndId_DropsTheRest()
    {
        var pdf = TestPdfs.OnePage(_dir, "id.pdf", Jpeg(), 10, 7.5);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        var at = text.LastIndexOf("trailer\n<< ", StringComparison.Ordinal) + "trailer\n<< ".Length;
        File.WriteAllBytes(pdf, Encoding.Latin1.GetBytes(text.Insert(at, "/ID [<0123456789ABCDEF> (\\200raw)] /Custom 5 ")));

        PdfRewriter.Write(pdf, Out(), None);

        var trailer = Trailer(File.ReadAllBytes(Out()));
        Assert.Equal(["ID", "Info", "Root", "Size"], trailer.Data.Keys.Order(StringComparer.Ordinal));
        var id = Assert.IsType<ArrayToken>(trailer.Data["ID"]);
        Assert.Equal(Convert.FromHexString("0123456789ABCDEF"), Assert.IsType<HexToken>(id.Data[0]).Bytes.ToArray());
        Assert.Equal([0x80, (byte)'r', (byte)'a', (byte)'w'], Assert.IsType<StringToken>(id.Data[1]).GetBytes());
        using var a = PdfDocument.Open(pdf);
        using var b = PdfDocument.Open(Out());
        Assert.Equal(a.Structure.Trailer.Root, b.Structure.Trailer.Root);
        Assert.Equal(a.Structure.Trailer.Info, b.Structure.Trailer.Info);
        Assert.Equal(a.Information.Producer, b.Information.Producer);
    }

    [Fact]
    public void Rewrite_EveryStreamGetsADirectLength()
    {
        // TestPdfs writes every /Length as an indirect object; the rewrite writes them direct (the objects stay).
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        Assert.Matches(@"/Length \d+ 0 R", RawText(pdf));

        PdfRewriter.Write(pdf, Out(), None);

        Assert.DoesNotMatch(@"/Length \d+ 0 R", RawText(Out()));
        AssertValidStructure(Out());
    }

    // ---- helpers ----

    private static PdfObjectId ImageId(string pdf) => Assert.Single(PdfImageScanner.Scan(pdf).Images).Image;

    private static string RawText(string pdf) => Encoding.Latin1.GetString(File.ReadAllBytes(pdf));

    private static long Key(IndirectReference r) => ((long)r.ObjectNumber << 16) + r.Generation;

    private static int Int(DictionaryToken d, string key) => Assert.IsType<NumericToken>(d.Data[key]).Int;

    /// <summary>A DCT image stream for the given JPEG bytes, with an optional <c>/Length</c> entry as given.</summary>
    private static PdfImageStream JpegStream(byte[] jpeg, int width, int height, string length = "") =>
        new(Encoding.ASCII.GetBytes(
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Filter /DCTDecode {length} >>"), jpeg);

    private static (string Entries, byte[] Data) GrayImage() =>
        ("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", [0, 80, 160, 255]);

    private static bool IsImage(StreamToken s) => s.StreamDictionary.Data.TryGetValue("Subtype", out var t) && t is NameToken { Data: "Image" };

    private static bool IsStructural(IToken token) =>
        token is StreamToken s && s.StreamDictionary.Data.TryGetValue("Type", out var t) && t is NameToken { Data: "ObjStm" or "XRef" };

    /// <summary>Every object the file's cross-reference data lists, as PdfPig reads it.</summary>
    private static Dictionary<IndirectReference, IToken> Objects(string pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.Structure.CrossReferenceTable.ObjectOffsets.Keys
            .ToDictionary(r => r, r => document.Structure.GetObject(r).Data);
    }

    /// <summary>Equal tokens: containers entry for entry, strings by their bytes, streams without <c>/Length</c> plus data.</summary>
    private static void AssertSame(IToken expected, IToken actual, string where)
    {
        switch (expected)
        {
            case StreamToken s:
                var t = Assert.IsType<StreamToken>(actual);
                AssertSame(Without(s.StreamDictionary, "Length"), Without(t.StreamDictionary, "Length"), where);
                Assert.True(s.Data.Span.SequenceEqual(t.Data.Span), $"{where}: stream data differs");
                Assert.Equal(t.Data.Length, Assert.IsType<NumericToken>(t.StreamDictionary.Data["Length"]).Int);
                break;
            case DictionaryToken d:
                var e = Assert.IsType<DictionaryToken>(actual);
                Assert.Equal(d.Data.Keys.Order(StringComparer.Ordinal), e.Data.Keys.Order(StringComparer.Ordinal));
                foreach (var (key, value) in d.Data) AssertSame(value, e.Data[key], $"{where} /{key}");
                break;
            case ArrayToken a:
                var b = Assert.IsType<ArrayToken>(actual);
                Assert.Equal(a.Data.Count, b.Data.Count);
                for (var i = 0; i < a.Data.Count; i++) AssertSame(a.Data[i], b.Data[i], $"{where}[{i}]");
                break;
            case StringToken str:
                Assert.Equal(str.GetBytes(), Assert.IsType<StringToken>(actual).GetBytes());
                break;
            case HexToken hex:
                Assert.Equal(hex.Bytes.ToArray(), Assert.IsType<HexToken>(actual).Bytes.ToArray());
                break;
            case NumericToken n:
                var m = Assert.IsType<NumericToken>(actual);
                Assert.Equal(n.Data, m.Data);
                Assert.Equal(n.Long, m.Long);
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }

    private static DictionaryToken Without(DictionaryToken d, string key) =>
        new(d.Data.Where(e => e.Key != key).ToDictionary(e => NameToken.Create(e.Key), e => e.Value));

    private readonly record struct XrefEntry(long Field1, int Generation, char Type);

    /// <summary>The classic table at <c>startxref</c>: one subsection from 0, 20-byte entries.</summary>
    private static List<XrefEntry> XrefEntries(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var start = long.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal("xref\n", text.Substring((int)start, 5));
        var header = Regex.Match(text[(int)start..], @"^xref\n0 (\d+)\n");
        Assert.True(header.Success, "one subsection starting at 0");
        var count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        var at = (int)start + header.Length;
        var entries = new List<XrefEntry>();
        for (var i = 0; i < count; i++, at += 20)
        {
            var entry = text.Substring(at, 20);
            Assert.Matches(@"^\d{10} \d{5} [nf]\r\n$", entry);
            entries.Add(new XrefEntry(long.Parse(entry[..10], CultureInfo.InvariantCulture),
                int.Parse(entry[11..16], CultureInfo.InvariantCulture), entry[17]));
        }
        Assert.StartsWith("trailer\n", text[at..]);
        return entries;
    }

    private static DictionaryToken Trailer(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var at = text.LastIndexOf("trailer\n", StringComparison.Ordinal) + "trailer\n".Length;
        var scanner = new UglyToad.PdfPig.Tokenization.Scanner.CoreTokenScanner(
            new MemoryInputBytes(bytes.AsMemory(at)), true, new StackDepthGuard(64));
        Assert.True(scanner.MoveNext());
        return Assert.IsType<DictionaryToken>(scanner.CurrentToken);
    }

    /// <summary>
    /// The file's structure as a strict reader needs it: one classic table whose in-use entries point at
    /// <c>n g obj</c>, a linked free list from entry 0 (65535) ending in 0, a trailer with <c>/Size</c> = entries and
    /// no <c>/Prev</c>, <c>/XRefStm</c> or <c>/Encrypt</c>, and a direct <c>/Length</c> equal to the data on every stream.
    /// </summary>
    private static void AssertValidStructure(string pdf)
    {
        var bytes = File.ReadAllBytes(pdf);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.Single(Regex.Matches(text, @"(?<![a-z])xref\n"));
        Assert.Single(Regex.Matches(text, "%%EOF"));
        var entries = XrefEntries(bytes);

        var free = new List<int>();
        for (var number = 0; number < entries.Count; number++)
        {
            var entry = entries[number];
            if (entry.Type == 'n')
            {
                var head = $"{number} {entry.Generation} obj";
                Assert.Equal(head, text.Substring((int)entry.Field1, head.Length));
                Assert.True(entry.Field1 == 0 || text[(int)entry.Field1 - 1] is '\n' or '\r', $"object {number} starts a line");
            }
            else
            {
                free.Add(number);
            }
        }
        Assert.Equal(0, free[0]);
        Assert.Equal(65535, entries[0].Generation);
        for (var i = 0; i < free.Count; i++)
            Assert.Equal(i + 1 < free.Count ? free[i + 1] : 0, entries[free[i]].Field1);

        var trailer = Trailer(bytes);
        Assert.Equal(entries.Count, Assert.IsType<NumericToken>(trailer.Data["Size"]).Int);
        Assert.IsType<IndirectReferenceToken>(trailer.Data["Root"]);
        foreach (var key in new[] { "Prev", "XRefStm", "Encrypt", "Type", "W", "Index", "Filter", "Length" })
            Assert.False(trailer.Data.ContainsKey(key), $"the trailer has /{key}");

        using var document = PdfDocument.Open(pdf);
        foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys)
        {
            if (document.Structure.GetObject(reference).Data is not StreamToken stream) continue;
            var length = Assert.IsType<NumericToken>(stream.StreamDictionary.Data["Length"]);
            Assert.Equal(stream.Data.Length, length.Int);
        }
        // The raw text agrees with the parsed lengths: "stream\n" + Length bytes + "\nendstream".
        var streams = Regex.Matches(text, @"/Length (\d+)\b[^\n]*?>>\nstream\n");
        Assert.NotEmpty(streams);
        Assert.Equal(Regex.Matches(text, "\nendstream\n").Count, streams.Count);
        foreach (Match m in streams)
        {
            var dataStart = m.Index + m.Length;
            var length = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.Equal("\nendstream", text.Substring(dataStart + length, 10));
        }
    }
}
