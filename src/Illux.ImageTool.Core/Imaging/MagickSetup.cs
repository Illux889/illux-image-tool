using ImageMagick;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Imaging;

/// <summary>Process-wide ImageMagick configuration (spec 8.4: limits from the machine's RAM, disk cache in temp).</summary>
public static class MagickSetup
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>
    /// Where ImageMagick puts its pixel cache once an image no longer fits in the memory limit: <c>IlluxImageTool</c>
    /// below the temp folder (TMPDIR on macOS; the folders job's is its private <c>folder-work/</c>, V15).
    /// </summary>
    public static string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "IlluxImageTool");

    /// <summary>
    /// Why <see cref="TempDirectory"/> must not be used, or null; asked by <see cref="Configure"/> and again before each
    /// file is processed (a file is refused rather than cached in an unsafe folder). Replaceable for tests.
    /// </summary>
    internal static Func<string?> TempCheck { get; set; } = () => CheckTempDirectory(TempDirectory);

    /// <summary>The problem with <see cref="TempDirectory"/> right now (see <see cref="TempCheck"/>), or null.</summary>
    public static string? TempDirectoryProblem() => TempCheck();

    /// <summary>
    /// Sets ImageMagick's memory limit to half of the memory available to this process and its temp directory
    /// to <see cref="TempDirectory"/> (when that passes <see cref="TempDirectoryProblem"/>). Safe to call repeatedly;
    /// only the first call has an effect.
    /// </summary>
    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured) return;

            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available > 0) ResourceLimits.Memory = (ulong)(available / 2);

            if (TempCheck() is null) MagickNET.SetTempDirectory(TempDirectory);
            _configured = true;
        }
    }

    /// <summary>
    /// Windows: creates the folder (as always) and accepts it. macOS (final review I2): the folder is created with mode
    /// 0700 when missing; it must then be a real folder (no link) owned by this account that group and others cannot
    /// write to, or another user could read or swap the pixel cache (as with a squatted <c>/tmp/IlluxImageTool</c>).
    /// </summary>
    internal static string? CheckTempDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return null;
        }
        if (!UnixNative.IsSupported) return $"ImageMagick's temporary folder '{path}' can only be checked on macOS on Apple silicon; files are not processed.";
        try
        {
            if (UnixNative.LStat(path) is null) Directory.CreateDirectory(path, OwnerOnly);
            return TrustedFolder.Problem(path, UnixNative.EffectiveUserId) is { } reason
                ? $"ImageMagick's temporary folder '{path}' {reason}; files are not processed until it is fixed."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"ImageMagick's temporary folder '{path}' cannot be used ({ex.Message}); files are not processed until it is fixed.";
        }
    }
}
