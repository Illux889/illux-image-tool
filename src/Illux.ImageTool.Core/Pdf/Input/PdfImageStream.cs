namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// A replacement image object: <paramref name="Dictionary"/> is the stream dictionary in PDF syntax
/// (<c>&lt;&lt; … &gt;&gt;</c>, with a direct <c>/Length</c>), <paramref name="DataPath"/> the file that holds the
/// encoded stream data (all of it, nothing else). Only the dictionary is held in memory; <see cref="PdfRewriter"/>
/// copies the data from the file into the new PDF.
/// </summary>
public sealed record PdfImageStream(byte[] Dictionary, string DataPath);
