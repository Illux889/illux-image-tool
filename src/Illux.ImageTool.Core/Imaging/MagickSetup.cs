using ImageMagick;

namespace Illux.ImageTool.Imaging;

/// <summary>Process-wide ImageMagick configuration (spec 8.4: limits from the machine's RAM, disk cache in temp).</summary>
public static class MagickSetup
{
    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>Where ImageMagick puts its pixel cache once an image no longer fits in the memory limit.</summary>
    public static string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "IlluxImageTool");

    /// <summary>
    /// Sets ImageMagick's memory limit to half of the memory available to this process and its temp directory
    /// to <see cref="TempDirectory"/>. Safe to call repeatedly; only the first call has an effect.
    /// </summary>
    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;

            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available > 0) ResourceLimits.Memory = (ulong)(available / 2);

            Directory.CreateDirectory(TempDirectory);
            MagickNET.SetTempDirectory(TempDirectory);
            _configured = true;
        }
    }
}
