namespace Illux.ImageTool.Pdf.Input;

/// <summary>
/// A replacement image object: <paramref name="Dictionary"/> is the stream dictionary in PDF syntax
/// (<c>&lt;&lt; … &gt;&gt;</c>, with a direct <c>/Length</c>), <paramref name="Data"/> the encoded stream data.
/// </summary>
public sealed record PdfImageStream(byte[] Dictionary, byte[] Data);
