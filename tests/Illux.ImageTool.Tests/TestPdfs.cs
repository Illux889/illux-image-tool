using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using Illux.ImageTool.Jpeg;
using Illux.ImageTool.Pdf;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Builds small test PDFs on the internal <see cref="PdfObjectWriter"/>. Each method writes <c>name</c> into
/// <c>dir</c> and returns its full path. Images are embedded unchanged: a JPEG as DCTDecode (byte for byte), anything
/// else as FlateDecode samples (8 or 16 bit, device colour space) with a soft mask when the image has alpha.
/// </summary>
internal static class TestPdfs
{
    private const double PointsPerCm = 72 / 2.54;

    /// <summary>
    /// One page sized to the drawn image: <paramref name="widthCm"/> × <paramref name="heightCm"/>, turned
    /// <paramref name="rotateDegrees"/> counter-clockwise through the page's <c>cm</c> (the page is the turned
    /// image's bounding box).
    /// </summary>
    public static string OnePage(string dir, string name, string imagePath, double widthCm, double heightCm,
        double rotateDegrees = 0, string version = "1.4") =>
        Write(dir, name, MinVersion(version, imagePath), pdf =>
        {
            var image = WriteImage(pdf, Encode(imagePath));
            var (matrix, pageWidth, pageHeight) = Rotated(widthCm * PointsPerCm, heightCm * PointsPerCm, rotateDegrees);
            return [new PageSpec(pageWidth, pageHeight, $"q {matrix} cm /Im0 Do Q", $"/Im0 {image} 0 R")];
        });

    /// <summary>16 bit samples need PDF 1.5; a lower requested version is raised to it.</summary>
    private static string MinVersion(string version, string imagePath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        if (bytes is [0xFF, 0xD8, 0xFF, ..]) return version;
        using var image = new MagickImage(imagePath);
        return image.Depth > 8 && string.CompareOrdinal(version, "1.5") < 0 ? "1.5" : version;
    }

    /// <summary>One page per entry, each with its own image drawn over the whole page.</summary>
    public static string Pages(string dir, string name, params (string imagePath, double widthCm, double heightCm)[] pages) =>
        Write(dir, name, "1.4", pdf => pages
            .Select(p => FullPage(WriteImage(pdf, Encode(p.imagePath)), p.widthCm, p.heightCm))
            .ToList());

    /// <summary>
    /// One page of <paramref name="widthCm"/> × <paramref name="heightCm"/>; the image is drawn inside a form XObject
    /// with <c>/Matrix [2 0 0 1 0 0]</c>, the form's content and the page each add a <c>cm</c>, and together they
    /// draw the image over the whole page.
    /// </summary>
    public static string InForm(string dir, string name, string imagePath, double widthCm, double heightCm) =>
        Write(dir, name, "1.4", pdf =>
        {
            var image = WriteImage(pdf, Encode(imagePath));
            var form = pdf.Reserve();
            pdf.WriteStream(form,
                $"/Type /XObject /Subtype /Form /BBox [0 0 1 2] /Matrix [2 0 0 1 0 0] /Resources << /XObject << /Im0 {image} 0 R >> >>",
                output => output.Write(Encoding.ASCII.GetBytes("q 1 0 0 2 0 0 cm /Im0 Do Q")));
            double w = widthCm * PointsPerCm, h = heightCm * PointsPerCm;
            return [new PageSpec(w, h, $"q {N(w / 2)} 0 0 {N(h / 2)} 0 0 cm /Fm0 Do Q", $"/Fm0 {form} 0 R")];
        });

    /// <summary>One image object drawn on two pages, over the whole page each time (page sizes in cm).</summary>
    public static string Reused(string dir, string name, string imagePath, (double, double) size1, (double, double) size2) =>
        Write(dir, name, "1.4", pdf =>
        {
            var image = WriteImage(pdf, Encode(imagePath));
            return [FullPage(image, size1.Item1, size1.Item2), FullPage(image, size2.Item1, size2.Item2)];
        });

    /// <summary>One 5 × 5 cm page with a 2 × 2 pixel grey inline image (<c>BI … ID … EI</c>) drawn over it.</summary>
    public static string WithInlineImage(string dir, string name) =>
        Write(dir, name, "1.4", _ =>
        {
            var side = N(5 * PointsPerCm);
            return [new PageSpec(5 * PointsPerCm, 5 * PointsPerCm,
                $"q {side} 0 0 {side} 0 0 cm\nBI /W 2 /H 2 /CS /G /BPC 8 ID \u0000\u0080\u0040\u00FF\nEI\nQ", "")];
        });

    /// <summary>One A4 page (21 × 29.7 cm) with a filled rectangle and no images.</summary>
    public static string NoImages(string dir, string name) =>
        Write(dir, name, "1.4", _ => [new PageSpec(21 * PointsPerCm, 29.7 * PointsPerCm, "0 0 1 rg 50 50 100 100 re f", "")]);

    /// <summary>
    /// One empty 5 × 5 cm page and a standard security handler dictionary in the trailer's <c>/Encrypt</c>. Nothing
    /// is actually encrypted; the file only has to look encrypted.
    /// </summary>
    public static string Encrypted(string dir, string name)
    {
        var encrypt = 0;
        var path = Write(dir, name, "1.4", pdf =>
        {
            encrypt = pdf.Reserve();
            var key = new string('A', 64);
            pdf.WriteObject(encrypt, $"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <{key}> /U <{key}> /P -3904 >>");
            return [new PageSpec(5 * PointsPerCm, 5 * PointsPerCm, "", "")];
        });

        // The trailer comes after the cross-reference table, so inserting entries there moves no object offsets.
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var anchor = text.LastIndexOf("trailer\n<< ", StringComparison.Ordinal);
        if (anchor < 0) throw new InvalidOperationException("The trailer anchor was not found in the test PDF.");
        var trailer = anchor + "trailer\n<< ".Length;
        var id = new string('0', 32);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text.Insert(trailer, $"/Encrypt {encrypt} 0 R /ID [<{id}> <{id}>] ")));
        return path;
    }

    /// <summary>
    /// One empty 5 × 5 cm page that looks signed: by default the catalog's AcroForm has <c>/SigFlags 3</c>; with
    /// <paramref name="byteRange"/> instead a signature dictionary with <c>/ByteRange</c> is in the file and the AcroForm
    /// has no flags. Nothing is actually signed.
    /// </summary>
    public static string Signed(string dir, string name, bool byteRange = false) =>
        Write(dir, name, "1.4", _ => [new PageSpec(5 * PointsPerCm, 5 * PointsPerCm, "", "")], pdf =>
        {
            if (!byteRange) return " /AcroForm << /Fields [] /SigFlags 3 >>";
            var signature = pdf.Reserve();
            pdf.WriteObject(signature, "<< /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 10 20 30] /Contents <00> >>");
            var field = pdf.Reserve();
            pdf.WriteObject(field, $"<< /FT /Sig /T (Signature1) /V {signature} 0 R >>");
            return $" /AcroForm << /Fields [{field} 0 R] >>";
        });

    /// <summary>
    /// One 5 x 5 cm page and an unreferenced object that does not parse (an unterminated dictionary), as damaged
    /// files sometimes carry.
    /// </summary>
    public static string WithBrokenObject(string dir, string name, string imagePath) =>
        Write(dir, name, "1.4", pdf =>
        {
            var image = WriteImage(pdf, Encode(imagePath));
            pdf.WriteObject(pdf.Reserve(), "<< /Broken [ (unterminated ");
            return [FullPage(image, 5, 5)];
        });

    /// <summary>
    /// One 5 x 5 cm page drawing the image with a degenerate <c>cm</c> (width 0); with <paramref name="alsoVisible"/>
    /// it is also drawn over the whole page.
    /// </summary>
    public static string WithDegenerateUse(string dir, string name, string imagePath, bool alsoVisible) =>
        Write(dir, name, "1.4", pdf =>
        {
            var image = WriteImage(pdf, Encode(imagePath));
            var side = N(5 * PointsPerCm);
            var visible = alsoVisible ? $" q {side} 0 0 {side} 0 0 cm /Im0 Do Q" : "";
            return [new PageSpec(5 * PointsPerCm, 5 * PointsPerCm, $"q 0 0 0 {side} 0 0 cm /Im0 Do Q{visible}", $"/Im0 {image} 0 R")];
        });

    /// <summary>
    /// One 5 x 5 cm page drawing the image through the given <c>cm</c> operators (e.g. matrices whose product is not a
    /// number).
    /// </summary>
    public static string WithMatrices(string dir, string name, string imagePath, string matrices) =>
        Write(dir, name, "1.4", pdf =>
        {
            var image = WriteImage(pdf, Encode(imagePath));
            return [new PageSpec(5 * PointsPerCm, 5 * PointsPerCm, $"q {matrices} /Im0 Do Q", $"/Im0 {image} 0 R")];
        });

    /// <summary>
    /// Like <see cref="OnePage"/> (5 x 5 cm, header <paramref name="headerVersion"/>), with <c>/Version</c> in the
    /// catalog.
    /// </summary>
    public static string WithCatalogVersion(string dir, string name, string imagePath, string headerVersion, string catalogVersion) =>
        Write(dir, name, headerVersion, pdf => [FullPage(WriteImage(pdf, Encode(imagePath)), 5, 5)],
            _ => $" /Version /{catalogVersion}");

    /// <summary>
    /// One 5 x 5 cm page drawing one image object built by <paramref name="image"/>: it returns the image dictionary's
    /// entries (without <c>/Length</c>) and its stream data, and may first write further objects (an ICC profile, a
    /// palette, a soft mask) with <see cref="WriteStream"/>.
    /// </summary>
    public static string CustomImage(string dir, string name, Func<PdfObjectWriter, (string Entries, byte[] Data)> image,
        string version = "1.5") =>
        Write(dir, name, version, pdf =>
        {
            var (entries, data) = image(pdf);
            return [FullPage(WriteStream(pdf, entries, data), 5, 5)];
        });

    /// <summary>
    /// One page per entry, each drawing one image object built by its <c>Image</c> (as <see cref="CustomImage"/>) over
    /// the whole page of <c>WidthCm</c> × <c>HeightCm</c>.
    /// </summary>
    public static string CustomPages(string dir, string name,
        params (Func<PdfObjectWriter, (string Entries, byte[] Data)> Image, double WidthCm, double HeightCm)[] pages) =>
        Write(dir, name, "1.5", pdf => pages
            .Select(p =>
            {
                var (entries, data) = p.Image(pdf);
                return FullPage(WriteStream(pdf, entries, data), p.WidthCm, p.HeightCm);
            })
            .ToList());

    /// <summary>
    /// Appends an incremental update to a classic-table <paramref name="pdf"/> (one written by this class): object
    /// <paramref name="redefined"/> gets the new body <paramref name="body"/> (same generation), object
    /// <paramref name="freed"/> is freed (an <c>f</c> entry for generation 1), and the new trailer has <c>/Prev</c>.
    /// Returns <paramref name="pdf"/>.
    /// </summary>
    public static string AppendUpdate(string pdf, int redefined, string body, int freed)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
        var trailer = Regex.Match(text, @"trailer\n<< /Size (\d+) /Root (\d+) 0 R /Info (\d+) 0 R >>\nstartxref\n(\d+)\n%%EOF\n$");
        if (!trailer.Success) throw new InvalidOperationException("Not a classic-table test PDF.");
        using var output = new FileStream(pdf, FileMode.Append, FileAccess.Write);
        var offset = output.Position;
        output.Write(Encoding.Latin1.GetBytes($"{redefined} 0 obj\n{body}\nendobj\n"));
        var xref = output.Position;
        var sections = new SortedDictionary<int, string>
        {
            [0] = $"{freed:D10} 65535 f\r\n",
            [redefined] = $"{offset:D10} 00000 n\r\n",
            [freed] = "0000000000 00001 f\r\n",
        };
        var table = new StringBuilder("xref\n");
        foreach (var (number, entry) in sections) table.Append(CultureInfo.InvariantCulture, $"{number} 1\n{entry}");
        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {trailer.Groups[1].Value} /Root {trailer.Groups[2].Value} 0 R /Info {trailer.Groups[3].Value} 0 R " +
            $"/Prev {trailer.Groups[4].Value} >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Encoding.Latin1.GetBytes(table.ToString()));
        return pdf;
    }

    /// <summary>
    /// A hand-built PDF 1.4 with a classic table, for raw syntax: objects 1-3 are the catalog, the page tree and one
    /// empty 100 × 100 pt page; <paramref name="objects"/> follow as given (Latin-1 text, each followed by LF). An
    /// object's cross-reference offset is where <c>n 0 obj</c> first appears in its own text (so text "14 0 obj …"
    /// for object 4 points one byte into the header), or, with null text, where it first appears in the file (inside
    /// another object).
    /// </summary>
    public static string Classic(string dir, string name, params (int Number, string? Text)[] objects)
    {
        List<(int Number, string? Text)> all =
        [
            (1, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj"),
            (2, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj"),
            (3, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>\nendobj"),
            .. objects,
        ];
        var file = new StringBuilder("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, text) in all.Where(o => o.Text != null))
        {
            var at = text!.IndexOf($"{number} 0 obj", StringComparison.Ordinal);
            if (at < 0) throw new ArgumentException($"Object {number}'s text has no \"{number} 0 obj\".");
            offsets[number] = file.Length + at;
            file.Append(text).Append('\n');
        }
        foreach (var (number, _) in all.Where(o => o.Text == null))
            offsets[number] = file.ToString().IndexOf($"{number} 0 obj", StringComparison.Ordinal);

        var xref = file.Length;
        var size = offsets.Keys.Max() + 1;
        file.Append(CultureInfo.InvariantCulture, $"xref\n0 {size}\n0000000000 65535 f\r\n");
        for (var number = 1; number < size; number++)
            file.Append(offsets.TryGetValue(number, out var offset) ? $"{offset:D10} 00000 n\r\n" : "0000000000 00000 f\r\n");
        file.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(file.ToString()));
        return path;
    }

    /// <summary>Writes one stream object and returns its number.</summary>
    public static int WriteStream(PdfObjectWriter pdf, string entries, byte[] data)
    {
        var number = pdf.Reserve();
        pdf.WriteStream(number, entries, output => output.Write(data));
        return number;
    }

    /// <summary>
    /// A PDF 1.6 page shaped like a CHILI Publish export: the page content sets an ExtGState and scales a form XObject
    /// slightly (1.0014 × 1.002; Chili writes factors like 1.00135 × 1.00202); the form (no <c>/Matrix</c>, resources
    /// by reference, Flate content) flips the y axis, clips to its <c>/BBox</c> and draws the JPEG with a
    /// negative-height <c>cm</c>. The JPEG (RGB or CMYK) is embedded as DCTDecode with an ICCBased colour space and an
    /// empty <c>/DecodeParms</c>. Together the transformations draw the image at <paramref name="widthCm"/> ×
    /// <paramref name="heightCm"/>, which is also the page (MediaBox = TrimBox = BleedBox, <c>/UserUnit 1</c>).
    /// With <paramref name="crLineEndings"/> the file's own lines end in CR, as real Chili exports do (stream data, the
    /// LF after <c>stream</c> and the CR LF of the table entries stay).
    /// </summary>
    public static string ChiliLike(string dir, string name, string imagePath, double widthCm, double heightCm,
        bool crLineEndings = false) =>
        Write(dir, name, "1.6", newline: crLineEndings ? "\r" : "\n", body: pdf =>
        {
            const double sx = 1.0014, sy = 1.002;
            var info = JpegInspector.Inspect(imagePath);
            var (icc, device) = info.Components switch
            {
                3 => (TestImages.SrgbIcc, "/DeviceRGB"),
                4 => (TestImages.CmykIcc, "/DeviceCMYK"),
                _ => throw new NotSupportedException("ChiliLike needs an RGB or CMYK JPEG."),
            };
            var profile = pdf.Reserve();
            pdf.WriteStream(profile, $"/N {info.Components} /Alternate {device} /Filter /FlateDecode", o => o.Write(Deflate(icc)));
            var image = pdf.Reserve();
            pdf.WriteStream(image,
                $"/Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} /ColorSpace [/ICCBased {profile} 0 R] " +
                "/BitsPerComponent 8 /Filter /DCTDecode" + (info.Components == 4 && info.HasAdobeMarker ? " /Decode [1 0 1 0 1 0 1 0]" : "") +
                " /DecodeParms << >>",
                o => o.Write(File.ReadAllBytes(imagePath)));

            double w = widthCm * PointsPerCm, h = heightCm * PointsPerCm;
            string bw = N(w / sx), bh = N(h / sy);
            var resources = pdf.Reserve();
            pdf.WriteObject(resources, $"<< /XObject << /Img1 {image} 0 R >> >>");
            var form = pdf.Reserve();
            var formContent = $" 1 0 0 -1 0 {bh} cm\n1 0 0 1 0 0 cm\nq\n0 0 m\n{bw} 0 l\n{bw} {bh} l\n0 {bh} l\n0 0 l\nh\nW\nn\n" +
                              $"q\n{bw} 0 0 -{bh} 0 {bh} cm\n/Img1 Do\nQ\nQ\n";
            pdf.WriteStream(form, $"/Type /XObject /Subtype /Form /Resources {resources} 0 R /BBox [0 0 {bw} {bh}] /Filter /FlateDecode",
                o => o.Write(Deflate(Encoding.ASCII.GetBytes(formContent))));
            var gs = pdf.Reserve();
            pdf.WriteObject(gs, "<< /Type /ExtGState /BM /Normal /OPM 1 >>");

            var box = $"[0 0 {N(w)} {N(h)}]";
            return [new PageSpec(w, h, $"q\n/GS1 gs\nq\n{N(sx)} 0 0 {N(sy)} 0 0 cm\n/Fm0 Do\nQ\nQ\n", $"/Fm0 {form} 0 R",
                $"/ExtGState << /GS1 {gs} 0 R >> ", $" /UserUnit 1 /TrimBox {box} /BleedBox {box}")];
        });

    /// <summary>
    /// A PDF 1.5 file as many exporters write it (the real Chili samples use a classic table instead): the image and
    /// content streams as plain objects, every other object (catalog, pages, page, info) in one compressed object
    /// stream, and a compressed cross-reference stream instead of a classic table and trailer. One page sized to the
    /// image drawn over it. <paramref name="packedExtras"/> are further object bodies put into the object stream
    /// (numbered after the others, referenced from nothing).
    /// </summary>
    public static string WithXrefStream(string dir, string name, string imagePath, double widthCm, double heightCm,
        bool signed = false, params string[] packedExtras)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var image = Encode(imagePath);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        new PdfObjectWriter(output).WriteHeader("1.5");

        var offsets = new SortedDictionary<int, long>();
        var next = 1;
        int WriteStream(string entries, byte[] data, int? reserved = null)
        {
            var number = reserved ?? next++;
            offsets[number] = output.Position;
            output.Write(Encoding.Latin1.GetBytes($"{number} 0 obj\n<< {entries} /Length {data.Length} >>\nstream\n"));
            output.Write(data);
            output.Write("\nendstream\nendobj\n"u8);
            return number;
        }

        var softMask = image.SoftMask is { } mask ? $" /SMask {WriteStream(mask.Entries, mask.Data)} 0 R" : "";
        var imageNumber = WriteStream(image.Entries + softMask, image.Data);
        double w = widthCm * PointsPerCm, h = heightCm * PointsPerCm;
        var contents = WriteStream("/Filter /FlateDecode", Deflate(Encoding.ASCII.GetBytes($"q {N(w)} 0 0 {N(h)} 0 0 cm /Im0 Do Q")));

        int catalog = next++, pages = next++, page = next++, info = next++;
        int signature = signed ? next++ : 0, field = signed ? next++ : 0;
        var acroForm = signed ? $" /AcroForm << /Fields [{field} 0 R] >>" : "";
        List<(int Number, string Body)> packed =
        [
            (catalog, $"<< /Type /Catalog /Pages {pages} 0 R{acroForm} >>"),
            (pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>"),
            (page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {N(w)} {N(h)}] " +
                   $"/Resources << /XObject << /Im0 {imageNumber} 0 R >> >> /Contents {contents} 0 R >>"),
            (info, "<< /Producer (TestPdfs) >>"),
        ];
        if (signed)
        {
            // A signature inside the object stream: no /SigFlags, and not a plain object in the file.
            packed.Add((signature, "<< /Type /Sig /Filter /Adobe.PPKLite /ByteRange [0 10 20 30] /Contents <00> >>"));
            packed.Add((field, $"<< /FT /Sig /T (Signature1) /V {signature} 0 R >>"));
        }
        foreach (var extra in packedExtras) packed.Add((next++, extra));
        var bodies = new StringBuilder();
        var header = new StringBuilder();
        foreach (var (number, body) in packed)
        {
            header.Append(CultureInfo.InvariantCulture, $"{number} {bodies.Length} ");
            bodies.Append(body).Append('\n');
        }
        var objectStream = WriteStream(
            $"/Type /ObjStm /N {packed.Count} /First {header.Length} /Filter /FlateDecode",
            Deflate(Encoding.ASCII.GetBytes(header.ToString() + bodies)));

        // Cross-reference stream (ISO 32000-1, 7.5.8): /W [1 4 2]; type 1 = offset, type 2 = in object stream.
        var xref = next++;
        var size = next;
        var rows = new byte[size * 7];
        void Row(int number, byte type, long field2, int field3)
        {
            var row = rows.AsSpan(number * 7, 7);
            row[0] = type;
            BinaryPrimitives.WriteUInt32BigEndian(row[1..5], checked((uint)field2));
            BinaryPrimitives.WriteUInt16BigEndian(row[5..7], checked((ushort)field3));
        }
        Row(0, 0, 0, 65535);
        foreach (var (number, offset) in offsets) Row(number, 1, offset, 0);
        for (var i = 0; i < packed.Count; i++) Row(packed[i].Number, 2, objectStream, i);
        var xrefOffset = output.Position;
        Row(xref, 1, xrefOffset, 0);
        WriteStream($"/Type /XRef /Size {size} /W [1 4 2] /Root {catalog} 0 R /Info {info} 0 R /Filter /FlateDecode", Deflate(rows), xref);
        output.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n"));
        return path;
    }

    /// <summary>
    /// A page: size in points, content stream text, the entries of its <c>/XObject</c> resources, further resource
    /// entries, and further page dictionary entries.
    /// </summary>
    private sealed record PageSpec(double Width, double Height, string Content, string XObjects,
        string MoreResources = "", string MoreEntries = "");

    /// <param name="Entries">The image dictionary without <c>/Length</c> and <c>/SMask</c>.</param>
    private sealed record EncodedImage(string Entries, byte[] Data, EncodedImage? SoftMask);

    private static PageSpec FullPage(int image, double widthCm, double heightCm)
    {
        double w = widthCm * PointsPerCm, h = heightCm * PointsPerCm;
        return new PageSpec(w, h, $"q {N(w)} 0 0 {N(h)} 0 0 cm /Im0 Do Q", $"/Im0 {image} 0 R");
    }

    /// <summary>Writes the header, the objects from <paramref name="body"/>, then the page tree, catalog, info and trailer.</summary>
    private static string Write(string dir, string name, string version, Func<PdfObjectWriter, IReadOnlyList<PageSpec>> body,
        Func<PdfObjectWriter, string>? catalogExtra = null, string newline = "\n")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var pdf = new PdfObjectWriter(output, newline);
        pdf.WriteHeader(version);
        var specs = body(pdf);
        var extra = catalogExtra?.Invoke(pdf) ?? "";

        int catalog = pdf.Reserve(), pages = pdf.Reserve(), info = pdf.Reserve();
        var kids = new List<int>();
        foreach (var spec in specs)
        {
            int page = pdf.Reserve(), contents = pdf.Reserve();
            pdf.WriteStream(contents, "", o => o.Write(Encoding.Latin1.GetBytes(spec.Content)));
            var xObjects = spec.XObjects.Length > 0 ? $"/XObject << {spec.XObjects} >> " : "";
            pdf.WriteObject(page,
                $"<< /Type /Page /Parent {pages} 0 R{spec.MoreEntries} /MediaBox [0 0 {N(spec.Width)} {N(spec.Height)}] " +
                $"/Resources << {xObjects}{spec.MoreResources}>> /Contents {contents} 0 R >>");
            kids.Add(page);
        }
        pdf.WriteObject(pages, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        pdf.WriteObject(catalog, $"<< /Type /Catalog /Pages {pages} 0 R{extra} >>");
        pdf.WriteObject(info, "<< /Producer (TestPdfs) >>");
        pdf.WriteTrailer(catalog, info);
        return path;
    }

    private static int WriteImage(PdfObjectWriter pdf, EncodedImage image)
    {
        var softMask = image.SoftMask is { } mask ? $" /SMask {WriteImage(pdf, mask)} 0 R" : "";
        var number = pdf.Reserve();
        pdf.WriteStream(number, image.Entries + softMask, output => output.Write(image.Data));
        return number;
    }

    private static EncodedImage Encode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes is [0xFF, 0xD8, 0xFF, ..])
        {
            var info = JpegInspector.Inspect(path);
            // As PdfImageWriter: an Adobe-marked 4-component JPEG stores inverted CMYK.
            var decode = info.Components == 4 && info.HasAdobeMarker ? " /Decode [1 0 1 0 1 0 1 0]" : "";
            return new EncodedImage(
                Entries(info.Width, info.Height, DeviceSpace(info.Components), 8) + " /Filter /DCTDecode" + decode, bytes, null);
        }

        using var image = new MagickImage(path);
        var (mapping, components) = image.ColorSpace switch
        {
            ColorSpace.CMYK => ("CMYK", 4),
            ColorSpace.Gray or ColorSpace.LinearGray => ("I", 1),
            _ => ("RGB", 3),
        };
        var bits = image.Depth > 8 ? 16 : 8;
        int width = (int)image.Width, height = (int)image.Height;
        var softMask = image.HasAlpha
            ? new EncodedImage(Entries(width, height, "/DeviceGray", bits) + " /Filter /FlateDecode", Deflate(Samples(image, "A", bits)), null)
            : null;
        return new EncodedImage(Entries(width, height, DeviceSpace(components), bits) + " /Filter /FlateDecode",
            Deflate(Samples(image, mapping, bits)), softMask);
    }

    private static string Entries(int width, int height, string colorSpace, int bits) =>
        $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent {bits}";

    private static string DeviceSpace(int components) => components switch
    {
        1 => "/DeviceGray",
        3 => "/DeviceRGB",
        4 => "/DeviceCMYK",
        _ => throw new NotSupportedException($"{components} colour components"),
    };

    /// <summary>The pixels as PDF samples: 8-bit bytes, or 16-bit big-endian.</summary>
    private static byte[] Samples(MagickImage image, string mapping, int bits)
    {
        using var pixels = image.GetPixels();
        if (bits == 8) return pixels.ToByteArray(mapping)!;
        var shorts = pixels.ToShortArray(mapping)!;
        var bytes = new byte[shorts.Length * 2];
        for (var i = 0; i < shorts.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), shorts[i]);
        return bytes;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var result = new MemoryStream();
        using (var zlib = new ZLibStream(result, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return result.ToArray();
    }

    /// <summary>
    /// The <c>cm</c> operands that draw the unit-square image at <paramref name="w"/> × <paramref name="h"/> points,
    /// turned <paramref name="degrees"/> counter-clockwise, moved so its bounding box starts at the origin; and that box.
    /// </summary>
    private static (string Matrix, double Width, double Height) Rotated(double w, double h, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        double cos = Clean(Math.Cos(radians)), sin = Clean(Math.Sin(radians));
        double a = w * cos, b = w * sin, c = -h * sin, d = h * cos;
        double[] xs = [0, a, c, a + c], ys = [0, b, d, b + d];
        double e = -xs.Min(), f = -ys.Min();
        var matrix = string.Join(' ', new[] { a, b, c, d, e, f }.Select(N));
        return (matrix, xs.Max() - xs.Min(), ys.Max() - ys.Min());
    }

    /// <summary>Snaps values within rounding noise of 0 or ±1 (cos 90° etc.) to the exact value.</summary>
    private static double Clean(double v) =>
        Math.Abs(v) < 1e-12 ? 0 : Math.Abs(Math.Abs(v) - 1) < 1e-12 ? Math.Sign(v) : v;

    private static string N(double value) => PdfObjectWriter.Number(value);
}
