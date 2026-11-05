using Illux.ImageTool.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// A colour space of image samples: <paramref name="Components"/> values per pixel, the ICC profile of an ICCBased
/// space, and for Indexed the palette (then <paramref name="Components"/> is 1, the index).
/// </summary>
internal sealed record PdfColorSpace(int Components, byte[]? Icc, PdfPalette? Palette);

/// <summary>An Indexed colour space's base (as written, and parsed), highest index and lookup table.</summary>
internal sealed record PdfPalette(IToken BaseToken, PdfColorSpace Base, int HighValue, byte[] Lookup);

/// <summary>
/// An image XObject as stored in a PDF (decision P2: the object is a <see cref="StreamToken"/> holding the raw data
/// and its dictionary), with the entries both directions of <see cref="PdfImageExtractor"/> and
/// <see cref="PdfImageEncoder"/> need.
/// </summary>
internal sealed class PdfImageObject
{
    public const string Dct = "DCTDecode";
    public const string Jpx = "JPXDecode";

    // Filters PdfPig decodes to raw samples, with their abbreviations (ISO 32000-1, tables 6 and 94).
    private static readonly Dictionary<string, string> FullNames = new(StringComparer.Ordinal)
    {
        ["ASCIIHexDecode"] = "ASCIIHexDecode", ["AHx"] = "ASCIIHexDecode",
        ["ASCII85Decode"] = "ASCII85Decode", ["A85"] = "ASCII85Decode",
        ["LZWDecode"] = "LZWDecode", ["LZW"] = "LZWDecode",
        ["FlateDecode"] = "FlateDecode", ["Fl"] = "FlateDecode",
        ["RunLengthDecode"] = "RunLengthDecode", ["RL"] = "RunLengthDecode",
        ["CCITTFaxDecode"] = "CCITTFaxDecode", ["CCF"] = "CCITTFaxDecode",
        ["DCTDecode"] = Dct, ["DCT"] = Dct,
        [Jpx] = Jpx,
    };

    private readonly PdfDocument _document;

    private PdfImageObject(PdfDocument document, StreamToken stream)
    {
        _document = document;
        Stream = stream;
        Width = PositiveInt("Width");
        Height = PositiveInt("Height");
        IsImageMask = Get("ImageMask") is BooleanToken { Data: true };
        Filters = FilterNames();
        Codec = Filters.Count > 0 && Filters[^1] is Dct or Jpx ? Filters[^1] : null;
        // JPX data carries its own bit depth; an image mask is always 1 bit.
        Bits = IsImageMask ? 1 : Get("BitsPerComponent") is NumericToken bits ? bits.Int : Codec == Jpx ? 0 : 8;
    }

    public static PdfImageObject Read(PdfDocument document, PdfObjectId id) =>
        new(document, document.Structure.GetObject(new IndirectReference(id.Number, id.Generation))?.Data as StreamToken
            ?? throw new InvalidDataException($"Object {id.Number} {id.Generation} R is not a stream."));

    public StreamToken Stream { get; }
    public DictionaryToken Dictionary => Stream.StreamDictionary;
    public int Width { get; }
    public int Height { get; }
    public int Bits { get; }
    public bool IsImageMask { get; }

    /// <summary>The <c>/Filter</c> names in decoding order, abbreviations spelled out.</summary>
    public IReadOnlyList<string> Filters { get; }

    /// <summary><c>DCTDecode</c> or <c>JPXDecode</c> when that is the last filter (an image file format), else null.</summary>
    public string? Codec { get; }

    /// <summary>The entry <paramref name="key"/> with indirect references followed, or null.</summary>
    public IToken? Get(string key) => Resolve(Dictionary.Data.TryGetValue(key, out var value) ? value : null);

    public IToken? Resolve(IToken? token)
    {
        for (var i = 0; token is IndirectReferenceToken reference && i < 32; i++)
            token = _document.Structure.GetObject(reference.Data)?.Data;
        return token is IndirectReferenceToken ? null : token;
    }

    /// <summary>The data with every filter applied except a final <see cref="Codec"/> (raw samples when there is none).</summary>
    public Memory<byte> Decode()
    {
        var count = Codec is null ? Filters.Count : Filters.Count - 1;
        var data = Stream.Data;
        if (count == 0) return data;

        foreach (var name in Filters.Take(count))
            if (!FullNames.ContainsKey(name) || name is Dct or Jpx) throw Unsupported(name);

        // The filters read their /DecodeParms from the dictionary, so references there must be followed first.
        var dictionary = Dictionary;
        if (Get("DecodeParms") is { } parms)
            dictionary = dictionary.With("DecodeParms", parms is ArrayToken array ? new ArrayToken(array.Data.Select(Resolve).Select(t => t ?? NullToken.Instance).ToList()) : parms);

        var filters = DefaultFilterProvider.Instance.GetNamedFilters(Filters.Take(count).Select(NameToken.Create).ToList());
        for (var i = 0; i < count; i++)
        {
            if (!filters[i].IsSupported) throw Unsupported(Filters[i]);
            data = filters[i].Decode(data, dictionary, DefaultFilterProvider.Instance, i);
        }
        return data;
    }

    /// <summary>The image's colour space (an image mask has none: one component, 1 bit).</summary>
    public PdfColorSpace ColorSpace() =>
        IsImageMask ? new PdfColorSpace(1, null, null) : ParseColorSpace(Get("ColorSpace"), allowIndexed: true);

    /// <summary><c>&lt;filter&gt; image data is not supported</c>.</summary>
    public static UnsupportedImageException Unsupported(string filter) => new($"{filter} image data is not supported");

    private PdfColorSpace ParseColorSpace(IToken? token, bool allowIndexed)
    {
        token = Resolve(token);
        if (token is ArrayToken { Length: 1 } single) token = Resolve(single.Data[0]);
        if (token is NameToken name)
        {
            return name.Data switch
            {
                "DeviceGray" or "G" => new PdfColorSpace(1, null, null),
                "DeviceRGB" or "RGB" => new PdfColorSpace(3, null, null),
                "DeviceCMYK" or "CMYK" => new PdfColorSpace(4, null, null),
                var other => throw UnsupportedColorSpace(other),
            };
        }
        if (token is not ArrayToken { Length: >= 2 } array || Resolve(array.Data[0]) is not NameToken family)
            throw new InvalidDataException("The image has no valid /ColorSpace.");

        switch (family.Data)
        {
            case "ICCBased":
                var profile = Resolve(array.Data[1]) as StreamToken ?? throw new InvalidDataException("An ICCBased colour space has no profile stream.");
                var n = Resolve(profile.StreamDictionary.Data.GetValueOrDefault("N")) as NumericToken
                        ?? throw new InvalidDataException("An ICC profile stream has no /N.");
                return new PdfColorSpace(n.Int, profile.Decode(_document.Structure.FilterProvider, _document.Structure.TokenScanner).ToArray(), null);
            case "CalGray":
            case "Separation":
                return new PdfColorSpace(1, null, null);
            case "CalRGB":
            case "Lab":
                return new PdfColorSpace(3, null, null);
            case "DeviceN":
                return new PdfColorSpace((Resolve(array.Data[1]) as ArrayToken)?.Length
                                         ?? throw new InvalidDataException("A DeviceN colour space has no colorant names."), null, null);
            case "Indexed" or "I" when allowIndexed && array.Length >= 4:
                var baseSpace = ParseColorSpace(array.Data[1], allowIndexed: false);
                var high = Resolve(array.Data[2]) as NumericToken ?? throw new InvalidDataException("An Indexed colour space has no highest index.");
                var lookup = Resolve(array.Data[3]) switch
                {
                    StringToken s => s.GetBytes(),
                    HexToken h => h.Bytes.ToArray(),
                    StreamToken s => s.Decode(_document.Structure.FilterProvider, _document.Structure.TokenScanner).ToArray(),
                    _ => throw new InvalidDataException("An Indexed colour space has no lookup table."),
                };
                return new PdfColorSpace(1, null, new PdfPalette(array.Data[1], baseSpace, Math.Clamp(high.Int, 0, 255), lookup));
            default:
                throw UnsupportedColorSpace(family.Data);
        }
    }

    private static UnsupportedImageException UnsupportedColorSpace(string name) => new($"the {name} colour space is not supported");

    private int PositiveInt(string key) =>
        Get(key) is NumericToken { Int: > 0 } value ? value.Int : throw new InvalidDataException($"The image has no valid /{key}.");

    private List<string> FilterNames()
    {
        var names = Get("Filter") switch
        {
            null => [],
            NameToken name => [name],
            ArrayToken array => array.Data.Select(Resolve).OfType<NameToken>().ToList(),
            _ => throw new InvalidDataException("The image has an invalid /Filter."),
        };
        return names.Select(n => FullNames.GetValueOrDefault(n.Data, n.Data)).ToList();
    }
}
