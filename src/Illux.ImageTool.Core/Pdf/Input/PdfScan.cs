namespace Illux.ImageTool.Pdf.Input;

/// <summary>An indirect object of a PDF: <c>Number Generation R</c>.</summary>
public readonly record struct PdfObjectId(int Number, int Generation);

/// <summary>
/// One drawn use of an image: the page (1-based), the image's index on that page (1-based, distinct images in drawing
/// order) and the drawn length (cm) of the image's width axis and of its height axis.
/// </summary>
public sealed record PdfImageUse(int Page, int Index, PdfObjectId Image, double WidthCm, double HeightCm);

/// <summary>
/// One distinct image object with its largest drawn size (by area) and the page and index of its first use.
/// <paramref name="DifferentSizes"/> is set when it is drawn at more than one size.
/// </summary>
public sealed record PdfImageEntry(PdfObjectId Image, int FirstPage, int FirstIndex, double WidthCm, double HeightCm, bool DifferentSizes);

/// <summary>What a PDF holds in images: its version (<c>1.4</c>), page count, drawn images and the number of inline images.</summary>
public sealed record PdfScan(string Version, int PageCount, IReadOnlyList<PdfImageEntry> Images, int InlineImages);
