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
/// <see cref="PdfRewriter"/> (ruling P1b): every unchanged object stored plainly in the input is copied byte for byte;
/// only replaced images and objects from object streams are written again, and those only when that is exact.
/// Unchanged objects are compared as raw bytes, each located through its file's own cross-reference data, and also
/// through PdfPig (dictionaries and arrays entry for entry, strings by their bytes, stream data byte for byte).
/// </summary>
public sealed class PdfRewriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "illux-pdfrewrite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string OutDir => Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

    private string Out(string name = "out.pdf") => Path.Combine(OutDir, name);

    private string Jpeg(string name = "a.jpg", int w = 60, int h = 45) => TestImages.Write(_dir, name, w, h, ColorSpace.sRGB, 8, 72);

    private static readonly Dictionary<PdfObjectId, PdfImageStream> None = [];

    private static Dictionary<PdfObjectId, PdfImageStream> Replace(PdfObjectId id, PdfImageStream stream) => new() { [id] = stream };

    // ---- the brief's tests ----

    [Fact]
    public void Rewrite_ReplacesOnlyGivenObjects()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg(), 10, 7.5);
        var id = ImageId(pdf);
        var newJpeg = File.ReadAllBytes(Jpeg("new.jpg", 120, 90));

        PdfRewriter.Write(pdf, Out(), Replace(id, JpegStream(newJpeg, 120, 90, "/Length 7")));

        var before = Objects(pdf);
        var after = Objects(Out());
        Assert.Equal(before.Keys.OrderBy(Key), after.Keys.OrderBy(Key));
        foreach (var (reference, token) in before)
            if (reference.ObjectNumber != id.Number) AssertSame(token, after[reference], reference.ToString());
        AssertRawSame(pdf, Out(), except: id.Number);
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
        Assert.Equal(before.Count, AssertRawSame(pdf, Out()));
        AssertValidStructure(Out());
    }

    [Fact]
    public void Rewrite_PageSizesAndContentUnchanged()
    {
        var pdf = TestPdfs.Pages(_dir, "pages.pdf", (Jpeg("p1.jpg"), 10, 7.5), (Jpeg("p2.jpg", 40, 80), 5, 10));
        var form = TestPdfs.InForm(_dir, "form.pdf", Jpeg("f.jpg"), 12, 9);
        var chili = TestPdfs.ChiliLike(_dir, "chili.pdf", Jpeg("c.jpg"), 10, 7.5, crLineEndings: true);
        var xref = TestPdfs.WithXrefStream(_dir, "xref.pdf", Jpeg("x.jpg"), 10, 7.5);

        foreach (var input in new[] { pdf, form, chili, xref })
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
                    if (a.Structure.GetObject(reference).Data is not StreamToken s || IsImage(s) || IsStructural(s)) continue;
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

        PdfRewriter.Write(pdf, Out(), Replace(id, JpegStream(newJpeg, 30, 20)));

        var raw = RawText(Out());
        Assert.StartsWith("%PDF-1.5\n", raw);
        Assert.DoesNotContain("/ObjStm", raw);
        Assert.DoesNotContain("/XRef", raw);
        Assert.DoesNotContain("/W [", raw);
        AssertValidStructure(Out());

        // Every object but the object stream and the xref stream is there, as a plain object; the plain ones byte for byte.
        var all = Objects(pdf);
        var before = all.Where(o => !IsStructural(o.Value)).ToDictionary();
        var after = Objects(Out());
        Assert.Equal(before.Keys.OrderBy(Key), after.Keys.OrderBy(Key));
        foreach (var (reference, token) in before)
            if (reference.ObjectNumber != id.Number) AssertSame(token, after[reference], reference.ToString());
        Assert.Equal(1, AssertRawSame(pdf, Out(), except: id.Number)); // the content stream
        // The dropped object stream and xref stream are free entries for their next generation.
        var entries = XrefEntries(File.ReadAllBytes(Out()));
        foreach (var dropped in all.Where(o => IsStructural(o.Value)).Select(o => o.Key))
            Assert.Equal(('f', 1), (entries[(int)dropped.ObjectNumber].Type, entries[(int)dropped.ObjectNumber].Generation));
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

        T Fails<T>(string input, IReadOnlyDictionary<PdfObjectId, PdfImageStream> replacements) where T : Exception
        {
            var ex = Assert.Throws<T>(() => PdfRewriter.Write(input, Out(), replacements));
            Assert.Empty(Directory.GetFiles(OutDir)); // neither the output nor a temporary file
            return ex;
        }

        // Caller errors: an object the file does not have (number or generation), a dictionary that is not exactly one.
        Fails<ArgumentException>(pdf, Replace(new PdfObjectId(999, 0), good));
        Fails<ArgumentException>(pdf, Replace(id with { Generation = 1 }, good));
        Fails<ArgumentException>(pdf, Replace(id, new("[1 2]"u8.ToArray(), jpeg)));
        Fails<ArgumentException>(pdf, Replace(id, new("<< /A 1 >> << /B 2 >>"u8.ToArray(), jpeg)));
        Fails<ArgumentException>(pdf, Replace(id, new("<< /A [1 2 >>"u8.ToArray(), jpeg)));
        Fails<ArgumentException>(pdf, Replace(id, new("<< /N 3000000000 >>"u8.ToArray(), jpeg)));
        // The input: missing, not a PDF, encrypted.
        Fails<FileNotFoundException>(Path.Combine(_dir, "missing.pdf"), None);
        File.WriteAllText(Path.Combine(_dir, "junk.pdf"), "not a PDF at all");
        Fails<InvalidDataException>(Path.Combine(_dir, "junk.pdf"), None);
        Assert.Equal("encrypted PDFs are not supported", Fails<NotSupportedException>(TestPdfs.Encrypted(_dir, "enc.pdf"), None).Message);
    }

    [Fact]
    public void Rewrite_FailureAfterWritingStarted_LeavesNoFile()
    {
        // The image (a plain object) is copied first; the refused name comes later, from the object stream.
        var pdf = TestPdfs.WithXrefStream(_dir, "late.pdf", Jpeg("big.jpg", 400, 300), 10, 7.5, packedExtras: "<< /caf#E9 1 >>");

        Assert.Throws<NotSupportedException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.Empty(Directory.GetFiles(OutDir));
    }

    [Fact]
    public void Rewrite_OlderFileAtOutput_KeptOnFailure_ReplacedOnSuccess()
    {
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        var late = TestPdfs.WithXrefStream(_dir, "late.pdf", Jpeg(), 10, 7.5, packedExtras: "<< /caf#E9 1 >>");
        File.WriteAllText(Out(), "an older output");

        Assert.Throws<ArgumentException>(() => PdfRewriter.Write(pdf, Out(),
            Replace(new PdfObjectId(999, 0), JpegStream(File.ReadAllBytes(Jpeg("n.jpg", 30, 20)), 30, 20))));
        Assert.Throws<NotSupportedException>(() => PdfRewriter.Write(late, Out(), None));

        Assert.Equal("an older output", File.ReadAllText(Out()));
        Assert.Single(Directory.GetFiles(OutDir));

        PdfRewriter.Write(pdf, Out(), None);

        Assert.StartsWith("%PDF-1.4", RawText(Out()));
        Assert.Single(Directory.GetFiles(OutDir));
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

    // ---- raw copy (ruling P1b) ----

    [Fact]
    public void Rewrite_ChiliLikeWithCrLineEndings_CopiesObjectsByteForByte()
    {
        var pdf = TestPdfs.ChiliLike(_dir, "chili-cr.pdf", Jpeg(), 10, 7.5, crLineEndings: true);
        Assert.StartsWith("%PDF-1.6\r", RawText(pdf));
        var id = ImageId(pdf);
        var newJpeg = File.ReadAllBytes(Jpeg("new.jpg", 120, 90));

        PdfRewriter.Write(pdf, Out(), Replace(id, JpegStream(newJpeg, 120, 90)));

        AssertValidStructure(Out());
        Assert.StartsWith("%PDF-1.6\n", RawText(Out()));
        Assert.Equal(Objects(pdf).Count - 1, AssertRawSame(pdf, Out(), except: id.Number));
        Assert.Contains("obj\r<<", RawText(Out())); // the copied objects keep their CR line ends
        using var document = PdfDocument.Open(Out());
        var image = Assert.Single(document.GetPage(1).GetImages());
        Assert.Equal(120, image.WidthInSamples);
        Assert.Equal(newJpeg, image.RawBytes.ToArray());
        var drawn = Assert.Single(PdfImageScanner.Scan(Out()).Images);
        Assert.Equal(10, drawn.WidthCm, 0.001);
        Assert.Equal(7.5, drawn.HeightCm, 0.001);
    }

    [Fact]
    public void Rewrite_PlainObjectWithTokensPdfPigReadsLossily_CopiedByteForByte()
    {
        // Names PdfPig decodes as Windows-1252 or UTF-8, raw CR and CR LF in literals, integers beyond 32 bits and 2^53.
        const string Body =
            "<< /F#80 1 /BaseFont /#82l#82r#83S#83V#83b#83N /caf#E9 2 /caf#C3#A9 3 /Raw (a\rb\r\nc) /Esc (x\\ry) " +
            "/Big 3000000000 /Huge 9007199254740993 /Real 1.001350000000000000001 /Tiny .000000000000000000000000000001234 >>";
        var pdf = TestPdfs.CustomImage(_dir, "tokens.pdf", w =>
        {
            w.WriteObject(w.Reserve(), Body);
            return GrayImage();
        });

        PdfRewriter.Write(pdf, Out(), None);

        Assert.Contains(Body, RawText(Out()));
        Assert.Equal(Objects(pdf).Count, AssertRawSame(pdf, Out()));
    }

    [Fact]
    public void Rewrite_ResourceNameWithHighByte_KeptWithItsContent()
    {
        // "/Im0" → "/#80" (same length, so the offsets stay): the resource key and the content's "Do" must still match.
        var pdf = Patch(TestPdfs.OnePage(_dir, "f80.pdf", Jpeg(), 10, 7.5), "/Im0", "/#80", expected: 2);
        var id = ImageId(pdf);
        var newJpeg = File.ReadAllBytes(Jpeg("new.jpg", 30, 20));

        PdfRewriter.Write(pdf, Out(), Replace(id, JpegStream(newJpeg, 30, 20)));

        var raw = RawText(Out());
        Assert.Contains("/XObject << /#80 ", raw);
        Assert.Contains("/#80 Do", raw);
        Assert.Equal(Objects(pdf).Count - 1, AssertRawSame(pdf, Out(), except: id.Number));
        using var document = PdfDocument.Open(Out());
        Assert.Equal(newJpeg, Assert.Single(document.GetPage(1).GetImages()).RawBytes.ToArray());
    }

    [Fact]
    public void Rewrite_InfoStringWithRawCr_KeptExactly()
    {
        var pdf = Patch(TestPdfs.OnePage(_dir, "cr.pdf", Jpeg(), 10, 7.5), "(TestPdfs)", "(T\rst\r\nPd)", expected: 1);

        PdfRewriter.Write(pdf, Out(), None);

        Assert.Contains("<< /Producer (T\rst\r\nPd) >>", RawText(Out()));
        Assert.Equal(Objects(pdf).Count, AssertRawSame(pdf, Out()));
    }

    [Fact]
    public void Rewrite_EndOfObject_FoundPastStringsCommentsAndNames()
    {
        var pdf = TestPdfs.CustomImage(_dir, "end.pdf", w =>
        {
            w.WriteObject(w.Reserve(), "<< /A (endobj) /B [(x\\)endobj (nested) stream)] /endobj 1 % endobj stream\n /C <656e646f626a> >>");
            TestPdfs.WriteStream(w, "/S (stream endstream endobj) /endstream true", "data with endstream\nendobj inside"u8.ToArray());
            w.WriteObject(w.Reserve(), "42");
            w.WriteObject(w.Reserve(), "(a string)");
            w.WriteObject(w.Reserve(), "[1 2 3]");
            return GrayImage();
        });

        PdfRewriter.Write(pdf, Out(), None);

        Assert.Equal(Objects(pdf).Count, AssertRawSame(pdf, Out()));
        AssertValidStructure(Out());
    }

    [Fact]
    public void Rewrite_StreamLengths_CopiedOnesKeepTheirIndirectLength_ReplacedGetsDirect()
    {
        // TestPdfs writes every /Length as an indirect object; copied streams keep it (and its object), the replacement is direct.
        var pdf = TestPdfs.OnePage(_dir, "one.pdf", Jpeg(), 10, 7.5);
        var id = ImageId(pdf);
        var jpeg = File.ReadAllBytes(Jpeg("n.jpg", 30, 20));

        PdfRewriter.Write(pdf, Out(), Replace(id, JpegStream(jpeg, 30, 20, "/Length 5 0 R")));

        var raw = RawText(Out());
        Assert.Matches(@"/Length \d+ 0 R >>\nstream\n", raw);
        Assert.Contains($"/Length {jpeg.Length} >>\nstream\n", raw);
        AssertValidStructure(Out());
    }

    [Fact]
    public void Rewrite_StreamLengthNotMatchingItsData_Throws()
    {
        // The 4-byte image's length object says 3: the bytes are not where the length says, so nothing is guessed.
        var pdf = Patch(TestPdfs.CustomImage(_dir, "len.pdf", _ => GrayImage()), " 0 obj\n4\nendobj", " 0 obj\n3\nendobj", expected: 1);

        var ex = Assert.Throws<InvalidDataException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.Matches(@"^object \d+ 0 R", ex.Message);
        Assert.Empty(Directory.GetFiles(OutDir));
    }

    // ---- the raw-slice scanner (fix round 2) ----

    [Theory]
    [InlineData("4 0 obj\n<< /T (a>b endobj) /U 1 >>\nendobj", null)]                  // ">" in a string after "<<"
    [InlineData("4 0 obj << /T (50% > 20%) >> endobj", "5 0 obj << /V 5 >> endobj")] // "%" in a string, one line
    [InlineData("4 0 obj\n<< /T (a>b stream) /Length 3 >>\nstream\nabc\nendstream\nendobj", null)]
    [InlineData("4 0 obj\n[/Xendobj 1 /U 2]\nendobj", null)]                           // a name ending in "endobj"
    [InlineData("4 0 obj\n<< /A << /B 1 >> /Xendobj 1 >>\nendobj", null)]
    [InlineData("4 0 obj\n<< /Length 5 0 R >>\nstream\r\nabc\r\nendstream\nendobj", "5 0 obj\n3\nendobj")] // CR LF, indirect /Length
    [InlineData("4 0 obj<</A(x)/B<0a>/C[1 2]>>endobj", "5 0 obj%comment\n(endobj)\nendobj")]
    public void Rewrite_RawSyntax_ObjectsCopiedExactly(string object4, string? object5)
    {
        var pdf = object5 is null
            ? TestPdfs.Classic(_dir, "raw.pdf", (4, object4))
            : TestPdfs.Classic(_dir, "raw.pdf", (4, object4), (5, object5));

        PdfRewriter.Write(pdf, Out(), None);

        var raw = RawText(Out());
        Assert.Contains("\n" + object4 + "\n", raw);
        Assert.Single(Regex.Matches(raw, @"(?<!\d)4 0 obj"));
        if (object5 != null)
        {
            Assert.Contains("\n" + object5 + "\n", raw);
            Assert.Single(Regex.Matches(raw, @"(?<!\d)5 0 obj"));
        }
        Assert.Equal(Objects(pdf).Count, AssertRawSame(pdf, Out()));
        AssertValidStructure(Out());
    }

    [Fact]
    public void Rewrite_ObjectRunningIntoAnother_Throws()
    {
        // Object 5's cross-reference entry points into object 4's string: object 4 would run past a known offset.
        var pdf = TestPdfs.Classic(_dir, "runon.pdf", (4, "4 0 obj\n(x 5 0 obj << /V 5 >> endobj y)\nendobj"), (5, null));

        var ex = Assert.Throws<InvalidDataException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.StartsWith("object 4 0 R: it runs past offset", ex.Message);
        Assert.Empty(Directory.GetFiles(OutDir));
    }

    [Fact]
    public void Rewrite_SomethingAfterEndobj_Throws()
    {
        var pdf = TestPdfs.Classic(_dir, "cut.pdf", (4, "4 0 obj\n<< /A 1 >>\nendobj garbage) >>"));

        var ex = Assert.Throws<InvalidDataException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.StartsWith("object 4 0 R: something other than whitespace or a comment follows its endobj", ex.Message);
    }

    [Fact]
    public void Rewrite_OffsetInsideAnotherHeader_Throws()
    {
        // Object 4's offset points at the "4" of "14 0 obj": "4 0 obj" is there, but not as a whole token.
        var pdf = TestPdfs.Classic(_dir, "mid.pdf", (4, "14 0 obj << /M 1 >> endobj"));

        var ex = Assert.Throws<InvalidDataException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.StartsWith("object 4 0 R: its \"n g obj\" is not at its cross-reference offset", ex.Message);
    }

    [Fact]
    public void Rewrite_IncrementalUpdate_NewestRevisionOnly()
    {
        var extra = 0;
        var pdf = TestPdfs.CustomImage(_dir, "inc.pdf", w =>
        {
            extra = w.Reserve();
            w.WriteObject(extra, "<< /Old true >>");
            return GrayImage();
        });
        int info;
        using (var document = PdfDocument.Open(pdf))
            info = (int)Assert.IsType<IndirectReferenceToken>(document.Structure.Trailer.Info).Data.ObjectNumber;
        TestPdfs.AppendUpdate(pdf, redefined: info, body: "<< /Producer (Updated) >>", freed: extra);

        PdfRewriter.Write(pdf, Out(), None);

        var raw = RawText(Out());
        Assert.Contains($"\n{info} 0 obj\n<< /Producer (Updated) >>\nendobj\n", raw);
        Assert.DoesNotContain("(TestPdfs)", raw);  // the older revision of the info object
        Assert.DoesNotContain("/Old true", raw);   // freed by the update
        AssertValidStructure(Out());
        var entries = XrefEntries(File.ReadAllBytes(Out()));
        Assert.Equal(('f', 1), (entries[extra].Type, entries[extra].Generation));
        using var output = PdfDocument.Open(Out());
        Assert.Equal("Updated", output.Information.Producer);
        Assert.Single(output.GetPage(1).GetImages());
    }

    // ---- objects from object streams (written again, only when exact) ----

    [Fact]
    public void Rewrite_ObjectStreamObject_WrittenExactly()
    {
        const string Body =
            @"<< /Real 1.00135 /Tiny 0.000001 /NegReal -.5 /Small 0.00000000000000000000000000001234 /Big 2147483647 " +
            @"/Doc (\200\201\240\351) /Bom (\376\377\000) /Esc (a\(b\)c\\d\n\t\000) /Hex <00FF10> /Name /A#23B#20C#2F " +
            @"/Arr [1 0 R null true false [] << >>] >>";
        var pdf = TestPdfs.WithXrefStream(_dir, "packed.pdf", Jpeg(), 10, 7.5, packedExtras: Body);

        PdfRewriter.Write(pdf, Out(), None);

        var reference = Objects(pdf).Single(o => o.Value is DictionaryToken d && d.Data.ContainsKey("Real")).Key;
        AssertSame(Objects(pdf)[reference], Objects(Out())[reference], "the packed object");
        Assert.Contains(
            @"<< /Real 1.00135 /Tiny 0.000001 /NegReal -0.5 /Small 0.00000000000000000000000000001234 /Big 2147483647 " +
            @"/Doc (\200\201\240\351) /Bom (\376\377\000) /Esc (a\(b\)c\\d\n\t\000) /Hex <00FF10> /Name /A#23B#20C#2F " +
            @"/Arr [1 0 R null true false [] << >>] >>", RawText(Out()));
    }

    [Theory]
    [InlineData("<< /XObject << /F#80 1 0 R >> >>")]
    [InlineData("<< /Type /Font /BaseFont /#82l#82r#83S#83V#83b#83N >>")]
    [InlineData("<< /Title (a\rb) >>")]
    [InlineData("<< /Title (a\r\nb) >>")]
    [InlineData("<< /N 3000000000 >>")]
    public void Rewrite_ObjectStreamObject_NotExactlyWritable_Refused(string body)
    {
        var pdf = TestPdfs.WithXrefStream(_dir, "refused.pdf", Jpeg(), 10, 7.5, packedExtras: body);

        var ex = Assert.Throws<NotSupportedException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.Matches(@"^object \d+ 0 R cannot be written back exactly \(", ex.Message);
        Assert.Empty(Directory.GetFiles(OutDir));
    }

    // ---- further behaviour ----

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

        PdfRewriter.Write(pdf, Out(), Replace(id, replacement));

        AssertValidStructure(Out());
        AssertRawSame(pdf, Out(), except: id.Number);
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
            PdfRewriter.Write(pdf, output, Replace(id, JpegStream(jpeg, 30, 20, length)));

            using var document = PdfDocument.Open(output);
            var image = Assert.IsType<StreamToken>(document.Structure.GetObject(new IndirectReference(id.Number, id.Generation)).Data);
            Assert.Equal(jpeg.Length, Assert.IsType<NumericToken>(image.StreamDictionary.Data["Length"]).Int);
            Assert.Equal(jpeg, image.Data.ToArray());
        }
    }

    [Fact]
    public void Rewrite_UnreadableObject_Throws_LeavesNoFile()
    {
        // An object PdfPig cannot read cannot be copied; the rewrite does not guess (the scan still accepts the file).
        var pdf = TestPdfs.WithBrokenObject(_dir, "broken.pdf", Jpeg());

        var ex = Assert.Throws<InvalidDataException>(() => PdfRewriter.Write(pdf, Out(), None));

        Assert.Matches(@"^object \d+ 0 R could not be read", ex.Message);
        Assert.Empty(Directory.GetFiles(OutDir));
    }

    [Fact]
    public void Rewrite_LinearizationDictionary_Dropped_FreeForNextGeneration()
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
        Assert.Equal(('f', 1), (entries[number].Type, entries[number].Generation));
        Assert.Equal(Objects(pdf).Count - 1, Objects(Out()).Count);
    }

    [Fact]
    public void Rewrite_LiteralIdWithCarriageReturn_WrittenAsHex()
    {
        // About 6% of random 16-byte literal IDs hold a 0x0D; /ID needs no exact text, so it is not refused.
        var pdf = TestPdfs.OnePage(_dir, "idcr.pdf", Jpeg(), 10, 7.5);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        var at = text.LastIndexOf("trailer\n<< ", StringComparison.Ordinal) + "trailer\n<< ".Length;
        File.WriteAllBytes(pdf, Encoding.Latin1.GetBytes(text.Insert(at, "/ID [(a\rb) (c\\rd)] ")));

        PdfRewriter.Write(pdf, Out(), None);

        Assert.Contains(" /ID [<610D62> <630D64>] >>", RawText(Out()));
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
        Assert.Equal([0x80, (byte)'r', (byte)'a', (byte)'w'], Assert.IsType<HexToken>(id.Data[1]).Bytes.ToArray());
        Assert.Contains(" /ID [<0123456789ABCDEF> <80726177>] >>", RawText(Out()));
        using var a = PdfDocument.Open(pdf);
        using var b = PdfDocument.Open(Out());
        Assert.Equal(a.Structure.Trailer.Root, b.Structure.Trailer.Root);
        Assert.Equal(a.Structure.Trailer.Info, b.Structure.Trailer.Info);
        Assert.Equal(a.Information.Producer, b.Information.Producer);
    }

    // ---- helpers ----

    private static PdfObjectId ImageId(string pdf) => Assert.Single(PdfImageScanner.Scan(pdf).Images).Image;

    private static string RawText(string pdf) => Encoding.Latin1.GetString(File.ReadAllBytes(pdf));

    private static long Key(IndirectReference r) => (r.ObjectNumber << 16) + r.Generation;

    private static int Int(DictionaryToken d, string key) => Assert.IsType<NumericToken>(d.Data[key]).Int;

    /// <summary>Replaces every <paramref name="from"/> by <paramref name="to"/> (same length, so no offset moves).</summary>
    private static string Patch(string pdf, string from, string to, int expected)
    {
        Assert.Equal(from.Length, to.Length);
        var text = RawText(pdf);
        Assert.Equal(expected, Regex.Matches(text, Regex.Escape(from)).Count);
        File.WriteAllBytes(pdf, Encoding.Latin1.GetBytes(text.Replace(from, to, StringComparison.Ordinal)));
        return pdf;
    }

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

    /// <summary>
    /// The raw bytes (<c>n g obj</c> … <c>endobj</c>) of every object stored plainly in the file, each located through
    /// the file's own cross-reference data: from its offset to the last <c>endobj</c> before the next object's offset.
    /// </summary>
    private static Dictionary<IndirectReference, byte[]> RawObjects(string pdf)
    {
        var bytes = File.ReadAllBytes(pdf);
        Dictionary<IndirectReference, long> offsets;
        using (var document = PdfDocument.Open(pdf))
        {
            offsets = document.Structure.CrossReferenceTable.ObjectOffsets
                .Where(o => o.Value.Type == XrefEntryType.File)
                .ToDictionary(o => o.Key, o => o.Value.Value1);
        }
        var starts = offsets.Values.Order().Append(bytes.Length).ToList();
        var result = new Dictionary<IndirectReference, byte[]>();
        foreach (var (reference, offset) in offsets)
        {
            var next = starts[starts.IndexOf(offset) + 1];
            var span = bytes.AsSpan((int)offset, (int)(next - offset));
            var end = span.LastIndexOf("endobj"u8);
            Assert.True(end > 0, $"{reference}: no endobj");
            var raw = span[..(end + 6)].ToArray();
            Assert.StartsWith($"{reference.ObjectNumber} {reference.Generation} obj", Encoding.Latin1.GetString(raw));
            result[reference] = raw;
        }
        return result;
    }

    /// <summary>
    /// Every object stored plainly in <paramref name="input"/> (other than <paramref name="except"/> and the dropped
    /// object/xref streams) is in <paramref name="output"/> with the same bytes. Returns how many were compared.
    /// </summary>
    private static int AssertRawSame(string input, string output, long except = -1)
    {
        var tokens = Objects(input);
        var before = RawObjects(input);
        var after = RawObjects(output);
        var compared = 0;
        foreach (var (reference, raw) in before)
        {
            if (reference.ObjectNumber == except || IsStructural(tokens[reference])
                || tokens[reference] is DictionaryToken d && d.Data.ContainsKey("Linearized")) continue;
            Assert.True(after.ContainsKey(reference), $"{reference} is not a plain object in the output");
            Assert.True(raw.AsSpan().SequenceEqual(after[reference]), $"{reference}: the bytes differ");
            compared++;
        }
        return compared;
    }

    /// <summary>Equal tokens: containers entry for entry, strings by their bytes, numbers by value, stream data byte for byte.</summary>
    private static void AssertSame(IToken expected, IToken actual, string where)
    {
        switch (expected)
        {
            case StreamToken s:
                var t = Assert.IsType<StreamToken>(actual);
                AssertSame(s.StreamDictionary, t.StreamDictionary, where);
                Assert.True(s.Data.Span.SequenceEqual(t.Data.Span), $"{where}: stream data differs");
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
    /// no <c>/Prev</c>, <c>/XRefStm</c> or <c>/Encrypt</c>, and on every stream a <c>/Length</c> (direct, or an
    /// indirect one on a copied stream) equal to its data.
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
                Assert.True(text[(int)entry.Field1 - 1] is '\n' or '\r', $"object {number} starts a line");
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
            var length = stream.StreamDictionary.Data["Length"] is IndirectReferenceToken r
                ? document.Structure.GetObject(r.Data).Data
                : stream.StreamDictionary.Data["Length"];
            Assert.Equal(stream.Data.Length, Assert.IsType<NumericToken>(length).Int);
        }
        // Streams the rewriter wrote itself: "stream\n" + Length bytes + "\nendstream".
        foreach (Match m in Regex.Matches(text, @"/Length (\d+) >>\nstream\n"))
        {
            var dataStart = m.Index + m.Length;
            var length = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.Equal("\nendstream", text.Substring(dataStart + length, 10));
        }
    }
}
