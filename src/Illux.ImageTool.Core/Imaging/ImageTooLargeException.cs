namespace Illux.ImageTool.Imaging;

/// <summary>Upsampling would exceed the maximum output size; thrown before any pixels are resampled.</summary>
public sealed class ImageTooLargeException(string message) : Exception(message);
