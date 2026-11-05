using Illux.ImageTool.Options;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>
/// The non-UI half of <see cref="FolderWindow"/>: a folder's options as the same parameter strings Automation Engine
/// passes, and the checks a folder must pass before it is saved.
/// </summary>
internal static class FolderSettings
{
    /// <summary>The parameter strings for the options. At least one operation must be on, otherwise the parser would read "none" as "all".</summary>
    internal static List<string> ToParameters(FolderOptions options)
    {
        if (!options.EightBit && !options.Resample && !options.Baseline)
            throw new InvalidOperationException("At least one operation must be on.");

        var parameters = new List<string>();
        if (options.EightBit) parameters.Add("8bit");
        if (options.Resample) parameters.Add("resample");
        if (options.Baseline) parameters.Add("baseline");
        if (options.Output != OutputMode.Same) parameters.Add("output=" + options.Output.ToString().ToLowerInvariant());
        if (options.MissingPpi != MissingPpiPolicy.Assume72)
            parameters.Add("missingppi=" + options.MissingPpi.ToString().ToLowerInvariant());
        if (options.Quality != ProcessingOptions.Default.JpegQuality) parameters.Add($"quality={options.Quality}");
        parameters.AddRange(options.Extra);
        return parameters;
    }

    /// <summary>The reverse of <see cref="ToParameters"/>. An unknown or invalid parameter throws <see cref="ParameterException"/>.</summary>
    internal static FolderOptions FromParameters(IEnumerable<string> parameters)
    {
        var list = parameters.ToList();
        var parsed = ParameterParser.Parse(list);
        var extra = list.Where(p => p.TrimStart().StartsWith("filter", StringComparison.OrdinalIgnoreCase)).Select(p => p.Trim()).ToList();
        return new FolderOptions(
            parsed.Operations.HasFlag(Operations.EightBit),
            parsed.Operations.HasFlag(Operations.Resample),
            parsed.Operations.HasFlag(Operations.Baseline),
            parsed.Output,
            parsed.MissingPpi,
            parsed.JpegQuality)
        {
            Extra = extra.Count == 0 ? [] : extra,
        };
    }

    /// <summary>A short message for the first problem found, or null when the folder can be saved.</summary>
    internal static string? Validate(WatchedFolder folder)
    {
        if (string.IsNullOrWhiteSpace(folder.Name)) return "Enter a name.";
        if (string.IsNullOrWhiteSpace(folder.In)) return "Choose the In folder.";
        if (string.IsNullOrWhiteSpace(folder.Out)) return "Choose the Out folder.";
        if (string.IsNullOrWhiteSpace(folder.Error)) return "Choose the Error folder.";

        var given = new List<(string Label, string Path)> { ("In", folder.In), ("Out", folder.Out), ("Error", folder.Error) };
        if (!string.IsNullOrWhiteSpace(folder.Originals)) given.Add(("Originals", folder.Originals));

        // Relative paths would be resolved against whatever folder the service runs in, and the watcher skips such a folder.
        var paths = new List<(string Label, string Path)>();
        foreach (var (label, path) in given)
        {
            var trimmed = path.Trim();
            string? full = null;
            try
            {
                if (Path.IsPathFullyQualified(trimmed)) full = Normalize(trimmed);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            if (full is null) return $"The {label} folder is not a full path ({FullPathExample}).";
            paths.Add((label, full));
        }
        for (var i = 0; i < paths.Count; i++)
        for (var j = i + 1; j < paths.Count; j++)
        {
            if (string.Equals(paths[i].Path, paths[j].Path, StringComparison.OrdinalIgnoreCase))
                return $"The {paths[i].Label} and {paths[j].Label} folders must be different.";
        }
        return null;
    }

    private static string FullPathExample => OperatingSystem.IsWindows()
        ? @"for example C:\Folder or \\server\share\Folder"
        : "for example /Users/Shared/Folder";

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
