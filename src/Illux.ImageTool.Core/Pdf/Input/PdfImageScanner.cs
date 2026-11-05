using System.Globalization;
using Illux.ImageTool.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.InlineImages;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// Finds every image a PDF draws and the size it is drawn at (decision P2): walks each page's parsed content
/// operations with the current transformation matrix, follows forms, and takes the drawn size of an image as the
/// lengths of its two image axes (the column norms of the matrix) times the page's <c>/UserUnit</c>.
/// </summary>
public static class PdfImageScanner
{
    private const double CmPerPoint = 2.54 / 72;
    private const int MaxFormDepth = 16;
    private const double SameSizeToleranceCm = 0.01;

    public static PdfScan Scan(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = PdfDocument.Open(stream, new ParsingOptions { SkipMissingFonts = true });
            if (document.IsEncrypted) throw new UnsupportedImageException("encrypted PDFs are not supported");
            return new Walker(document).Scan(HeaderVersion(stream));
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

    private static string FirstLine(string message) =>
        message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";

    /// <summary>The version text of the <c>%PDF-x.y</c> header (within the first 1024 bytes), or null.</summary>
    private static string? HeaderVersion(Stream stream)
    {
        var buffer = new byte[1024];
        stream.Position = 0;
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

    private sealed class Walker(PdfDocument document)
    {
        private static readonly ILog Log = new NullLog();

        private readonly List<PdfImageUse> _uses = [];
        private int _inline;

        public PdfScan Scan(string? headerVersion)
        {
            CheckNotSigned();
            for (var number = 1; number <= document.NumberOfPages; number++) ScanPage(number);
            var header = headerVersion ?? document.Version.ToString("0.0", CultureInfo.InvariantCulture);
            var catalogVersion = (Resolve(Get(document.Structure.Catalog.CatalogDictionary, "Version")) as NameToken)?.Data;
            return new PdfScan(HigherVersion(header, catalogVersion), document.NumberOfPages, Aggregate(), _inline);
        }

        private void ScanPage(int number)
        {
            var page = document.GetPage(number);
            var userUnit = Resolve(Get(page.Dictionary, "UserUnit")) is NumericToken n && n.Double > 0 ? n.Double : 1;
            var resources = PageResources(page.Dictionary);
            var pageUses = new List<(PdfObjectId Image, double Width, double Height)>();
            Walk(page.Operations, resources, Matrix.Identity, 0, [], (id, m) =>
                pageUses.Add((id, m.WidthAxis * userUnit * CmPerPoint, m.HeightAxis * userUnit * CmPerPoint)));

            var order = new Dictionary<PdfObjectId, int>();
            foreach (var (id, width, height) in pageUses)
            {
                // An image axis of length 0 (degenerate cm) is invisible and would give an infinite ppi.
                if (width <= 0 || height <= 0) continue;
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
                    case BeginInlineImage:
                        _inline++;
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
            // A name missing from the resources draws nothing in a viewer either.
            if (Resolve(Get(resources, "XObject")) is not DictionaryToken xObjects) return;
            if (!xObjects.Data.TryGetValue(name, out var entry) || entry is not IndirectReferenceToken reference) return;
            if (Resolve(reference) is not StreamToken stream) return;

            var subtype = (Resolve(Get(stream.StreamDictionary, "Subtype")) as NameToken)?.Data;
            if (subtype == "Image")
            {
                onImage(new PdfObjectId(checked((int)reference.Data.ObjectNumber), reference.Data.Generation), ctm);
            }
            else if (subtype == "Form" && depth < MaxFormDepth && activeForms.Add(reference.Data))
            {
                try
                {
                    var matrix = Resolve(Get(stream.StreamDictionary, "Matrix")) is ArrayToken { Data.Count: >= 6 } array
                        ? ToMatrix(array) : Matrix.Identity;
                    var formResources = Resolve(Get(stream.StreamDictionary, "Resources")) as DictionaryToken ?? resources;
                    var bytes = stream.Decode(document.Structure.FilterProvider, document.Structure.TokenScanner);
                    var operations = new PageContentParser(ReflectionGraphicsStateOperationFactory.Instance,
                        new StackDepthGuard(64), true).Parse(1, new MemoryInputBytes(bytes), Log);
                    Walk(operations, formResources, matrix.Then(ctm), depth + 1, activeForms, onImage);
                }
                finally
                {
                    activeForms.Remove(reference.Data);
                }
            }
        }

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

        /// <summary>A signed PDF (AcroForm /SigFlags bit 1, or a signature dictionary with /ByteRange) cannot be rewritten.</summary>
        private void CheckNotSigned()
        {
            if (Resolve(Get(document.Structure.Catalog.CatalogDictionary, "AcroForm")) is DictionaryToken acroForm
                && Resolve(Get(acroForm, "SigFlags")) is NumericToken flags
                && ((int)flags.Double & 1) != 0)
                throw new UnsupportedImageException("signed PDFs are not supported");

            foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys)
            {
                // One object that will not parse (e.g. unreferenced garbage) must not make the whole PDF unreadable.
                try
                {
                    if (document.Structure.GetObject(reference)?.Data is DictionaryToken dictionary
                        && dictionary.Data.ContainsKey("ByteRange"))
                        throw new UnsupportedImageException("signed PDFs are not supported");
                }
                catch (UnsupportedImageException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }
        }

        private List<PdfImageEntry> Aggregate()
        {
            var entries = new List<PdfImageEntry>();
            foreach (var group in _uses.GroupBy(u => u.Image))
            {
                var first = group.First();
                var largest = group.MaxBy(u => u.WidthCm * u.HeightCm)!;
                var different = group.Any(u => Math.Abs(u.WidthCm - largest.WidthCm) > SameSizeToleranceCm
                                               || Math.Abs(u.HeightCm - largest.HeightCm) > SameSizeToleranceCm);
                entries.Add(new PdfImageEntry(group.Key, first.Page, first.Index, largest.WidthCm, largest.HeightCm, different));
            }
            return entries;
        }
    }
}
