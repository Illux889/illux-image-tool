namespace Illux.ImageTool.Imaging;

/// <summary>The input is not an image this tool processes (format, frame count or JPEG coding); the message says why.</summary>
public sealed class UnsupportedImageException : Exception
{
    public UnsupportedImageException(string reason) : base(reason) { }
    public UnsupportedImageException(string reason, Exception inner) : base(reason, inner) { }
}
