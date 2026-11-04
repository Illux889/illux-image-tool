using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The icon of the windows, the tray (Windows) and the menu bar (macOS): a blue rounded square with a white "I", drawn
/// at run time so no image file has to ship. Call only after Avalonia has started.
/// </summary>
internal static class AppIcon
{
    private const int Size = 64;

    private static WindowIcon? _icon;

    public static WindowIcon Get() => _icon ??= Draw();

    private static WindowIcon Draw()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var blue = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0xA8));
            context.DrawRectangle(blue, null, new Rect(2, 2, Size - 4, Size - 4), 12, 12);
            context.DrawRectangle(Brushes.White, null, new Rect(26, 16, 12, 32)); // stem
            context.DrawRectangle(Brushes.White, null, new Rect(18, 12, 28, 7)); // top serif
            context.DrawRectangle(Brushes.White, null, new Rect(18, 45, 28, 7)); // bottom serif
        }

        // Through PNG: every platform's icon implementation reads that. The stream is left open for the icon.
        var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new WindowIcon(png);
    }
}
