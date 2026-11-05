using System.Globalization;
using Illux.ImageTool.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.InlineImages;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Finds every image a PDF draws and the size it is drawn at (decision P2): walks each page's parsed content
/// operations with the current transformation matrix, follows forms, and takes the drawn size of an image as the
/// lengths of its two image axes (the column norms of the matrix) times the page's <c>/UserUnit</c>. Each form is
/// parsed once; more than 100,000 <c>Do</c> operators in one document fail the scan
/// (<c>the PDF could not be read (too many nested drawings)</c>). Image objects the walk does not reach are counted
/// (ruling Q16).
/// </summary>
public static class PdfImageScanner
{
    private const double CmPerPoint = 2.54 / 72;
    private const int MaxFormDepth = 16;
    private const double SameSizeToleranceCm = 0.01;
    private const int MaxDrawExpansions = 100_000;

    public static PdfScan Scan(string path)
    {
        if (SourceImage.IsIllustratorName(path)) throw new UnsupportedImageException(SourceImage.IllustratorMessage);
        try
        {
            using var stream = File.OpenRead(path);
            var headerVersion = HeaderVersion(stream);
            stream.Position = 0;
            using var document = PdfDocument.Open(stream, new ParsingOptions { SkipMissingFonts = true });
            if (document.IsEncrypted) throw new UnsupportedImageException("encrypted PDFs are not supported");
            return new Walker(document).Scan(headerVersion);
        }
        catch (UnsupportedImageException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new UnsupportedImageException("encrypted PDFs are not supported");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new UnsupportedImageException($"the PDF could not be read ({FirstLine(ex.Message)})", ex);
        }
    }

    internal static string FirstLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";

    /// <summary>The version text of the <c>%PDF-x.y</c> header (within the first 1024 bytes from the stream's position), or null.</summary>
    internal static string? HeaderVersion(Stream stream)
    {
        var buffer = new byte[1024];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = System.Text.Encoding.Latin1.GetString(buffer, 0, read);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"%PDF-(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The higher of the header version and the catalog's <c>/Version</c> (compared as major.minor).</summary>
    private static string HigherVersion(string header, string? catalog)
    {
        static bool TryParse(string? v, out Version parsed) => Version.TryParse(v, out parsed!);
        return TryParse(catalog, out var c) && TryParse(header, out var h) && c > h ? catalog! : header;
    }

    /// <summary>A PDF matrix (row vectors): <c>[a b 0; c d 0; e f 1]</c>.</summary>
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

        /// <summary><c>this × o</c>: apply this first, then o.</summary>
        public Matrix Then(Matrix o) => new(
            A * o.A + B * o.C, A * o.B + B * o.D,
            C * o.A + D * o.C, C * o.B + D * o.D,
            E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);

        public double WidthAxis => Math.Sqrt(A * A + B * B);
        public double HeightAxis => Math.Sqrt(C * C + D * D);
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }
        public void Debug(string message, Exception ex) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
    }

    /// <summary>A page as only its dictionary (see <see cref="PageDictionaryFactory"/>).</summary>
    private sealed record PageDictionary(DictionaryToken Dictionary);

    /// <summary>Gives PdfPig's page lookup (page tree, numbering) without PdfPig processing the page's content.</summary>
    private sealed class PageDictionaryFactory : IPageFactory<PageDictionary>
    {
        public PageDictionary Create(int number, DictionaryToken dictionary, PageTreeMembers pageTreeMembers,
            NamedDestinations namedDestinations) => new(dictionary);
    }

    private sealed class Walker(PdfDocument document)
    {
        private static readonly ILog Log = new NullLog();

        private readonly List<PdfImageUse> _uses = [];
        private int _inline;

        // Every image object in the file, the page thumbnails and each image's /SMask and /Mask objects (noted by
        // CheckSupported), and the images the walk reaches (ruling Q16).
        private readonly HashSet<PdfObjectId> _allImages = [];
        private readonly HashSet<PdfObjectId> _thumbnails = [];
        private readonly Dictionary<PdfObjectId, List<PdfObjectId>> _masks = [];
        private readonly HashSet<PdfObjectId> _reached = [];

        // Each form is parsed once (its inline images are counted then), however often it is drawn; every Do counts
        // towards the cap, so forms that draw each other many times over cannot keep the walk busy.
        private readonly Dictionary<IndirectReference, Form> _forms = [];
        private int _expansions;

        private sealed record Form(IReadOnlyList<IGraphicsStateOperation> Operations, Matrix Matrix, DictionaryToken? Resources);

        public PdfScan Scan(string? headerVersion)
        {
            CheckSupported();
            // PdfPig's own Page would run the page's whole content, forms included, before the walk starts (and so is
            // not bounded by the cap); only the page dictionary is taken from it.
            document.AddPageFactory(new PageDictionaryFactory());
            for (var number = 1; number <= document.NumberOfPages; number++) ScanPage(number);
            var header = headerVersion ?? document.Version.ToString("0.0", CultureInfo.InvariantCulture);
            var catalogVersion = (Resolve(Get(document.Structure.Catalog.CatalogDictionary, "Version")) as NameToken)?.Data;
            return new PdfScan(HigherVersion(header, catalogVersion), document.NumberOfPages, Aggregate(), _inline, NotDrawn());
        }

        private void ScanPage(int number)
        {
            var page = document.GetPage<PageDictionary>(number).Dictionary;
            var userUnit = Resolve(Get(page, "UserUnit")) is NumericToken n && n.Double > 0 ? n.Double : 1;
            var resources = PageResources(page);
            var operations = PageOperations(number, page);
            var pageUses = new List<(PdfObjectId Image, double Width, double Height)>();
            _inline += operations.Count(o => o is BeginInlineImage);
            Walk(operations, resources, Matrix.Identity, 0, [], (id, m) =>
                pageUses.Add((id, m.WidthAxis * userUnit * CmPerPoint, m.HeightAxis * userUnit * CmPerPoint)));

            var order = new Dictionary<PdfObjectId, int>();
            foreach (var (id, width, height) in pageUses)
            {
                // An image axis of length 0 (degenerate cm) is invisible and would give an infinite ppi; an infinite or
                // NaN length (from an overflowing cm) is no size at all.
                if (!(double.IsFinite(width) && width > 0) || !(double.IsFinite(height) && height > 0)) continue;
                if (!order.TryGetValue(id, out var index)) order[id] = index = order.Count + 1;
                _uses.Add(new PdfImageUse(number, index, id, width, height));
            }
        }

        private void Walk(IReadOnlyList<IGraphicsStateOperation> operations, DictionaryToken? resources, Matrix start,
            int depth, HashSet<IndirectReference> activeForms, Action<PdfObjectId, Matrix> onImage)
        {
            var ctm = start;
            var saved = new Stack<Matrix>();
            foreach (var operation in operations)
            {
                switch (operation)
                {
                    case Push:
                        saved.Push(ctm);
                        break;
                    case Pop:
                        if (saved.Count > 0) ctm = saved.Pop();
                        break;
                    case ModifyCurrentTransformationMatrix cm when cm.Value.Length >= 6:
                        ctm = new Matrix(cm.Value[0], cm.Value[1], cm.Value[2], cm.Value[3], cm.Value[4], cm.Value[5]).Then(ctm);
                        break;
                    case InvokeNamedXObject invoke:
                        Invoke(invoke.Name.Data, resources, ctm, depth, activeForms, onImage);
                        break;
                }
            }
        }

        private void Invoke(string name, DictionaryToken? resources, Matrix ctm, int depth,
            HashSet<IndirectReference> activeForms, Action<PdfObjectId, Matrix> onImage)
        {
            if (++_expansions > MaxDrawExpansions)
                throw new UnsupportedImageException("the PDF could not be read (too many nested drawings)");

            // A name missing from the resources draws nothing in a viewer either.
            if (Resolve(Get(resources, "XObject")) is not DictionaryToken xObjects) return;
            if (!xObjects.Data.TryGetValue(name, out var entry) || entry is not IndirectReferenceToken reference) return;
            if (Resolve(reference) is not StreamToken stream) return;

            var subtype = (Resolve(Get(stream.StreamDictionary, "Subtype")) as NameToken)?.Data;
            if (subtype == "Image")
            {
                var id = Id(reference.Data);
                _reached.Add(id);
                onImage(id, ctm);
            }
            else if (subtype == "Form" && depth < MaxFormDepth && activeForms.Add(reference.Data))
            {
                try
                {
                    var form = ParsedForm(reference.Data, stream);
                    Walk(form.Operations, form.Resources ?? resources, form.Matrix.Then(ctm), depth + 1, activeForms, onImage);
                }
                finally
                {
                    activeForms.Remove(reference.Data);
                }
            }
        }

        /// <summary>
        /// The page's content operations: its <c>/Contents</c> stream, or the streams of a <c>/Contents</c> array joined
        /// by a line feed (as PdfPig's own page does).
        /// </summary>
        private IReadOnlyList<IGraphicsStateOperation> PageOperations(int number, DictionaryToken page)
        {
            var streams = Resolve(Get(page, "Contents")) switch
            {
                StreamToken single => [single],
                ArrayToken array => array.Data.Select(Resolve).OfType<StreamToken>().ToList(),
                _ => new List<StreamToken>(),
            };
            if (streams.Count == 0) return [];
            using var bytes = new MemoryStream();
            for (var i = 0; i < streams.Count; i++)
            {
                if (i > 0) bytes.WriteByte((byte)'\n');
                bytes.Write(streams[i].Decode(document.Structure.FilterProvider, document.Structure.TokenScanner).Span);
            }
            return Parse(number, bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
        }

        private static IReadOnlyList<IGraphicsStateOperation> Parse(int pageNumber, ReadOnlyMemory<byte> content) =>
            new PageContentParser(ReflectionGraphicsStateOperationFactory.Instance, new StackDepthGuard(64), true)
                .Parse(pageNumber, new MemoryInputBytes(content), Log);

        /// <summary>The form's operations, <c>/Matrix</c> and own <c>/Resources</c>, parsed on its first use.</summary>
        private Form ParsedForm(IndirectReference reference, StreamToken stream)
        {
            if (_forms.TryGetValue(reference, out var form)) return form;
            var matrix = Resolve(Get(stream.StreamDictionary, "Matrix")) is ArrayToken { Data.Count: >= 6 } array
                ? ToMatrix(array) : Matrix.Identity;
            var resources = Resolve(Get(stream.StreamDictionary, "Resources")) as DictionaryToken;
            var operations = Parse(1, stream.Decode(document.Structure.FilterProvider, document.Structure.TokenScanner));
            _inline += operations.Count(o => o is BeginInlineImage);
            return _forms[reference] = new Form(operations, matrix, resources);
        }

        private static PdfObjectId Id(IndirectReference reference) => new(checked((int)reference.ObjectNumber), reference.Generation);

        private static Matrix ToMatrix(ArrayToken array)
        {
            var v = array.Data.Take(6).Select(t => t is NumericToken n ? n.Double : 0).ToArray();
            return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
        }

        private static IToken? Get(DictionaryToken? dictionary, string key) =>
            dictionary != null && dictionary.Data.TryGetValue(key, out var value) ? value : null;

        /// <summary>The page's <c>/Resources</c> (a dictionary or a reference to one), or the nearest ancestor's.</summary>
        private DictionaryToken? PageResources(DictionaryToken page)
        {
            var node = page;
            for (var i = 0; node != null && i < 64; i++)
            {
                if (Resolve(Get(node, "Resources")) is DictionaryToken resources) return resources;
                node = Resolve(Get(node, "Parent")) as DictionaryToken;
            }
            return null;
        }

        private IToken? Resolve(IToken? token)
        {
            for (var i = 0; token is IndirectReferenceToken reference && i < 32; i++)
                token = document.Structure.GetObject(reference.Data)?.Data;
            return token is IndirectReferenceToken ? null : token;
        }

        /// <summary>
        /// Visits every object once. Refuses signed PDFs (ruling Q6a: a signature dictionary with <c>/ByteRange</c>;
        /// <c>/SigFlags</c> alone only marks a signature field, which may be blank) and Illustrator files (ruling Q12a:
        /// <c>AIPrivateData</c> entries in any object; <c>/PieceInfo /Illustrator</c> alone is not refused); a signature
        /// wins. Also notes every image object, each image's <c>/SMask</c> and <c>/Mask</c> objects and the page
        /// thumbnails, for <see cref="NotDrawn"/>.
        /// </summary>
        private void CheckSupported()
        {
            var illustrator = false;
            foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys)
            {
                // One object that will not parse (e.g. unreferenced garbage) must not make the whole PDF unreadable.
                try
                {
                    var token = document.Structure.GetObject(reference)?.Data;
                    var dictionary = token switch
                    {
                        DictionaryToken d => d,
                        StreamToken s => s.StreamDictionary,
                        _ => null,
                    };
                    if (dictionary is null) continue;
                    if (dictionary.Data.ContainsKey("ByteRange"))
                        throw new UnsupportedImageException("signed PDFs are not supported");
                    illustrator |= dictionary.Data.Keys.Any(k => k.StartsWith("AIPrivateData", StringComparison.Ordinal));
                    if (Get(dictionary, "Thumb") is IndirectReferenceToken thumbnail) _thumbnails.Add(Id(thumbnail.Data));
                    if (token is StreamToken && (Resolve(Get(dictionary, "Subtype")) as NameToken)?.Data == "Image")
                    {
                        var id = Id(reference);
                        _allImages.Add(id);
                        _masks[id] = new[] { "SMask", "Mask" }
                            .Select(key => Get(dictionary, key))
                            .OfType<IndirectReferenceToken>()
                            .Select(mask => Id(mask.Data))
                            .ToList();
                    }
                }
                catch (UnsupportedImageException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Deliberately ignored: an object PdfPig cannot parse is not a signature a reader would honour either.
                }
            }
            if (illustrator) throw new UnsupportedImageException(SourceImage.IllustratorMessage);
        }

        /// <summary>
        /// Ruling Q16: the image objects the walk did not reach, other than the <c>/SMask</c> or <c>/Mask</c> of an image
        /// it did reach and the page thumbnails: images used only by annotation appearances, patterns or soft-mask
        /// groups, or not at all. They are not processed; the pipeline warns about them.
        /// </summary>
        private int NotDrawn()
        {
            var accounted = new HashSet<PdfObjectId>(_reached);
            foreach (var image in _reached)
                if (_masks.TryGetValue(image, out var masks)) accounted.UnionWith(masks);
            accounted.UnionWith(_thumbnails);
            return _allImages.Count(image => !accounted.Contains(image));
        }

        /// <summary>
        /// One entry per image: where it is first drawn, and (ruling Q17) the longest drawn width axis and the longest
        /// drawn height axis of all its uses, so it is below the target nowhere.
        /// </summary>
        private List<PdfImageEntry> Aggregate()
        {
            var entries = new List<PdfImageEntry>();
            foreach (var group in _uses.GroupBy(u => u.Image))
            {
                var first = group.First();
                var width = group.Max(u => u.WidthCm);
                var height = group.Max(u => u.HeightCm);
                var different = group.Any(u => Math.Abs(u.WidthCm - width) > SameSizeToleranceCm
                                               || Math.Abs(u.HeightCm - height) > SameSizeToleranceCm);
                entries.Add(new PdfImageEntry(group.Key, first.Page, first.Index, width, height, different));
            }
            return entries;
        }
    }
}
