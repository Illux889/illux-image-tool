using System.Text;

namespace Illux.ImageTool.Settings;

/// <summary>Reads the end of the newest <c>watcher-YYYYMMDD.log</c> for the settings window.</summary>
internal static class ServiceLog
{
    public const int MaxChars = 200_000;

    /// <summary>The last <paramref name="maxChars"/> characters of the newest log, or null when there is none or it cannot be read.</summary>
    public static string? ReadLatest(string directory, int maxChars = MaxChars)
    {
        try
        {
            var newest = new DirectoryInfo(directory).EnumerateFiles("watcher-*.log")
                .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (newest is null) return null;

            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var maxBytes = (long)maxChars * 4;
            if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var tail = text.Length > maxChars ? text[^maxChars..] : text;
            return $"{newest.FullName}{Environment.NewLine}{Environment.NewLine}{tail}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
