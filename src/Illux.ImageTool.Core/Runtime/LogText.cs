namespace Illux.ImageTool.Runtime;

/// <summary>
/// Text from outside (file names above all: on macOS a name may hold a line break) as it goes into a log line, so it
/// can neither split the line nor forge another one (final review C1, both systems).
/// </summary>
public static class LogText
{
    /// <summary><paramref name="text"/> without control characters (<see cref="char.IsControl(char)"/>); null gives "".</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var c in text)
        {
            if (char.IsControl(c)) return string.Concat(text.Where(ch => !char.IsControl(ch)));
        }
        return text;
    }

    /// <summary>
    /// A multi-line text (an exception with its stack trace) as lines that each start with four spaces and hold no
    /// other control characters, so none of them can pass for a log entry of its own.
    /// </summary>
    public static string Indented(string? text)
    {
        var lines = (text ?? "").Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(line => "    " + Clean(line));
        return string.Join(Environment.NewLine, lines);
    }
}
