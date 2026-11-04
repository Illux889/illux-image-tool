using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Illux.ImageTool.Options;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Watch;

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads and writes <c>config.json</c> (by default <see cref="AppPaths.ConfigFile"/>).</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// A missing file gives the default configuration; unreadable JSON or a <c>PollSeconds</c> below 1 throws
    /// <see cref="ConfigException"/> (a zero or negative poll interval would make the watcher spin or fail).
    /// </summary>
    public static WatcherConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        if (!File.Exists(path)) return new WatcherConfig();

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var config = JsonSerializer.Deserialize<WatcherConfig>(stream, Json)
                ?? throw new ConfigException($"Configuration file '{path}' is empty.");
            if (config.PollSeconds < 1)
                throw new ConfigException($"Configuration file '{path}': pollSeconds must be at least 1 (found {config.PollSeconds}).");
            config.Folders ??= [];
            config.ResolutionRules ??= [.. Options.ResolutionRules.Defaults];
            var problems = Options.ResolutionRules.Validate(config.ResolutionRules);
            if (problems.Count > 0)
                throw new ConfigException($"Configuration file '{path}': invalid resolution rules: {string.Join(" ", problems)}");
            foreach (var folder in config.Folders) folder.Parameters ??= [];
            return config;
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Configuration file '{path}' is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The resolution rules for a processing run; never throws. A missing file or section gives the defaults silently,
    /// an unreadable or invalid file gives the defaults plus a warning to show.
    /// </summary>
    public static (IReadOnlyList<ResolutionRule> Rules, string? Warning) LoadRulesForRun(string? path = null)
    {
        try
        {
            return (Load(path).ResolutionRules, null);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            return (Options.ResolutionRules.Defaults, $"{ex.Message} Using the default resolution rules instead.");
        }
    }

    /// <summary>Writes through a temporary file so a reader never sees a half-written configuration.</summary>
    public static void Save(WatcherConfig config, string? path = null) => Save(config, path, onRetry: null);

    /// <param name="onRetry">Called with the attempt number after a replace failed and before waiting; for tests.</param>
    internal static void Save(WatcherConfig config, string? path, Action<int>? onRetry)
    {
        ArgumentNullException.ThrowIfNull(config);
        path ??= AppPaths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
            // The service reads config.json while the window saves it; a reader holding it open makes the replace fail briefly (R33).
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
                {
                    onRetry?.Invoke(attempt);
                    Thread.Sleep(50);
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// macOS, for the settings app (V7): writes the existing <c>config.json</c> (root:admin 0664, made by the installer)
    /// in place, because Root is root-owned 0755 and a temporary file next to it cannot be created. Never follows a
    /// link and never creates the file; a reader may briefly see a half-written file, which it treats like any other
    /// invalid configuration (the service keeps its last good rules).
    /// </summary>
    /// <exception cref="IOException">The file is missing, a link, not a regular file with one name, or cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not write it (not in the admin group).</exception>
    [SupportedOSPlatform("macos")]
    public static void SaveInPlace(WatcherConfig config) => SaveInPlace(config, AppPaths.ConfigFile, RootUserId);

    /// <summary>The installer makes Root (the folder of <c>config.json</c>) root-owned.</summary>
    public const uint RootUserId = 0;

    /// <summary>As <see cref="SaveInPlace(WatcherConfig)"/> for <paramref name="path"/>, whose folder must be owned by <paramref name="expectedFolderOwner"/> (tests run as an ordinary user).</summary>
    [SupportedOSPlatform("macos")]
    internal static void SaveInPlace(WatcherConfig config, string path, uint expectedFolderOwner)
    {
        ArgumentNullException.ThrowIfNull(config);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, Json));
        try
        {
            UnixNative.WriteInPlace(path, bytes);
        }
        catch (FileNotFoundException ex)
        {
            throw new IOException($"'{path}' is missing. Reinstall Illux Image Tool to create it.", ex);
        }
    }
}
