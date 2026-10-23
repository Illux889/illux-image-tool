using System.Text.Json.Serialization;

namespace Illux.ImageTool.Watch;

/// <summary>One watched folder with its own folders and AE-style parameters (spec 7).</summary>
public sealed class WatchedFolder
{
    public string Name { get; set; } = "";

    public string In { get; set; } = "";

    public string Out { get; set; } = "";

    public string Error { get; set; } = "";

    /// <summary>Where untouched inputs go after success; null means an <c>Originals</c> folder next to <see cref="In"/>.</summary>
    public string? Originals { get; set; }

    /// <summary>The same parameter strings Automation Engine passes (<c>8bit</c>, <c>upsample=300</c>, …).</summary>
    public List<string> Parameters { get; set; } = [];

    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string OriginalsOrDefault => Originals ?? DefaultOriginals(In);

    private static string DefaultOriginals(string inFolder)
    {
        // A trailing separator would make GetDirectoryName return In itself instead of its parent.
        var trimmed = inFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return Path.Combine(string.IsNullOrEmpty(parent) ? trimmed : parent, "Originals");
    }
}

public sealed class WatcherConfig
{
    public List<WatchedFolder> Folders { get; set; } = [];

    /// <summary>False stops the periodic update check (a manual "check now" still works).</summary>
    public bool CheckForUpdates { get; set; } = true;

    public int PollSeconds { get; set; } = 5;
}
