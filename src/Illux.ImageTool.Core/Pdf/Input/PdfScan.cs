namespace Illux.ImageTool.Pdf.Input;

/// <summary>An indirect object of a PDF: <c>Number Generation R</c>.</summary>
public readonly record struct PdfObjectId(int Number, int Generation);

/// <summary>
/// One drawn use of an image: the page (1-based), the image's index on that page (1-based, distinct images in drawing
/// order) and the drawn length (cm) of the image's width axis and of its height axis.
/// </summary>
public sealed record PdfImageUse(int Page, int Index, PdfObjectId Image, double WidthCm, double HeightCm);

/// <summary>
/// One distinct image object with the page and index of its first use and its largest drawn length per axis (ruling
/// Q17: the longest width axis and the longest height axis of all its uses, which may come from different uses).
/// <paramref name="DifferentSizes"/> is set when it is drawn at more than one size.
/// </summary>
public sealed record PdfImageEntry(PdfObjectId Image, int FirstPage, int FirstIndex, double WidthCm, double HeightCm, bool DifferentSizes);

/// <summary>
/// What a PDF holds in images: its version (<c>1.4</c>), page count, drawn images, the number of inline images (each
/// counted once per content stream or form that holds it) and the number of image objects the drawing walk does not
/// reach (ruling Q16: not drawn, not the <c>/SMask</c> or <c>/Mask</c> of a drawn image, not a page <c>/Thumb</c>).
/// </summary>
public sealed record PdfScan(string Version, int PageCount, IReadOnlyList<PdfImageEntry> Images, int InlineImages, int NotDrawnImages);
