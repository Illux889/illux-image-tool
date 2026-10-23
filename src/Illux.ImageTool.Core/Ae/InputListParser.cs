using System.Text.RegularExpressions;

namespace Illux.ImageTool.Ae;

/// <summary>Splits the single input-list string that Automation Engine passes as %1 into file paths.</summary>
public static class InputListParser
{
    // A colon separates entries only when it starts a new drive path ("D:\") or UNC path ("\\server"),
    // so the drive letter of "C:\a.jpg" is never mistaken for a separator.
    private static readonly Regex ColonSeparator = new(
        @":(?=(?:[A-Za-z]:[\\/]|\\\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Split(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var parts = raw.Contains(';')
            ? raw.Split(';')
            : ColonSeparator.Split(raw);

        var paths = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var path = part.Trim().Trim('"').Trim();
            if (path.Length > 0) paths.Add(path);
        }

        return paths;
    }
}
