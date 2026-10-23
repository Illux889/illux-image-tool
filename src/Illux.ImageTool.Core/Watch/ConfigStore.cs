using System.Text.Json;
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
            foreach (var folder in config.Folders) folder.Parameters ??= [];
            return config;
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"Configuration file '{path}' is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>Writes through a temporary file so a reader never sees a half-written configuration.</summary>
    public static void Save(WatcherConfig config, string? path = null)
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
                    Thread.Sleep(50);
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
