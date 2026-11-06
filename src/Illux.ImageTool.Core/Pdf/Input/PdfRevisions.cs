using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.CrossReference;
using UglyToad.PdfPig.Tokens;

namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// The objects an incremental update freed. PdfPig merges the cross-reference sections of all revisions into
/// <c>ObjectOffsets</c> but leaves out free entries, so an object freed in a later revision still shows up there with
/// its older offset. This walks the chain of sections from the newest (the one no other section's <c>/Prev</c> points
/// to) to the oldest and takes, per object number, the newest entry; a section's own entries come from the file (a
/// classic table is read as text, a cross-reference stream is decoded), and a hybrid file's stream (<c>/XRefStm</c>)
/// counts as part of its table's revision, where an in-use entry wins over a free one.
/// </summary>
internal static class PdfRevisions
{
    /// <summary>Object number → the generation of its free entry, for every object whose newest entry is free.</summary>
    public static IReadOnlyDictionary<long, int> Freed(Structure structure, PdfRawObjects raw)
    {
        var parts = structure.CrossReferenceTable.Parts;
        var revisions = parts.Where(p => p.TiedToXrefAtOffset == null).ToList();
        if (revisions.Count == 0) return new Dictionary<long, int>();
        var byOffset = revisions.GroupBy(p => p.Offset).ToDictionary(g => g.Key, g => g.First());
        var heads = revisions.Where(r => !revisions.Any(o => o.Previous == r.Offset)).ToList();
        if (heads.Count != 1)
            throw new InvalidDataException("the cross-reference sections do not form one chain of revisions");

        var newest = new Dictionary<long, (int Generation, bool InUse)>();
        var visited = new HashSet<long>();
        for (var revision = heads[0]; revision != null && visited.Add(revision.Offset);
             revision = revision.Previous is { } previous ? byOffset.GetValueOrDefault(previous) : null)
        {
            var entries = new Dictionary<long, (int Generation, bool InUse)>();
            foreach (var part in parts.Where(p => p == revision || p.TiedToXrefAtOffset == revision.Offset))
            {
                foreach (var (number, generation, inUse) in Entries(structure, raw, part))
                    if (!entries.TryGetValue(number, out var known) || (inUse && !known.InUse))
                        entries[number] = (generation, inUse);
            }
            foreach (var (number, entry) in entries) newest.TryAdd(number, entry);
        }
        return newest.Where(e => !e.Value.InUse && e.Key > 0).ToDictionary(e => e.Key, e => e.Value.Generation);
    }

    private static IEnumerable<(long Number, int Generation, bool InUse)> Entries(Structure structure, PdfRawObjects raw,
        CrossReferenceTablePart part) =>
        part.Type == CrossReferenceType.Table ? raw.ReadClassicSection(part.Offset) : StreamEntries(structure, part);

    /// <summary>The rows of a cross-reference stream (ISO 32000-1, 7.5.8): type 0 is free, 1 and 2 are in use.</summary>
    private static IEnumerable<(long Number, int Generation, bool InUse)> StreamEntries(Structure structure, CrossReferenceTablePart part)
    {
        var location = structure.CrossReferenceTable.ObjectOffsets
            .Where(o => o.Value.Type == XrefEntryType.File && o.Value.Value1 == part.Offset)
            .Select(o => (IndirectReference?)o.Key)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"the cross-reference stream at offset {part.Offset} is not a known object");
        if (structure.GetObject(location).Data is not StreamToken stream)
            throw new InvalidDataException($"the cross-reference stream at offset {part.Offset} is not a stream");
        var dictionary = stream.StreamDictionary;
        var widths = dictionary.Data.GetValueOrDefault("W") is ArrayToken w && w.Data.Count == 3 && w.Data.All(t => t is NumericToken)
            ? w.Data.Cast<NumericToken>().Select(n => n.Int).ToArray()
            : throw new InvalidDataException($"the cross-reference stream at offset {part.Offset} has no valid /W");
        var size = dictionary.Data.GetValueOrDefault("Size") is NumericToken s ? s.Long : 0;
        var index = dictionary.Data.GetValueOrDefault("Index") is ArrayToken i && i.Data.All(t => t is NumericToken)
            ? i.Data.Cast<NumericToken>().Select(n => n.Long).ToArray()
            : [0, size];
        var data = stream.Decode(structure.FilterProvider, structure.TokenScanner).ToArray();
        var rowLength = widths.Sum();
        if (rowLength <= 0 || index.Length % 2 != 0)
            throw new InvalidDataException($"the cross-reference stream at offset {part.Offset} has an invalid layout");

        var result = new List<(long, int, bool)>();
        var row = 0;
        for (var section = 0; section < index.Length; section += 2)
        {
            for (var k = 0L; k < index[section + 1]; k++, row++)
            {
                if ((row + 1) * rowLength > data.Length)
                    throw new InvalidDataException($"the cross-reference stream at offset {part.Offset} is shorter than its /Index");
                var at = row * rowLength;
                var type = widths[0] == 0 ? 1 : Field(data, at, widths[0]);
                var third = Field(data, at + widths[0] + widths[1], widths[2]);
                result.Add((index[section] + k, type == 0 ? (int)third : 0, type != 0));
            }
        }
        return result;
    }

    private static long Field(byte[] data, int at, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++) value = (value << 8) | data[at + i];
        return value;
    }
}
