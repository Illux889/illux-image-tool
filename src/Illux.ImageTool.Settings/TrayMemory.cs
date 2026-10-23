using System.Text.Json;

namespace Illux.ImageTool.Settings;

/// <summary>Remembers, per version, that the user chose "Later" in the tray popup (<c>%LOCALAPPDATA%\Illux Image Tool\tray.json</c>).</summary>
internal sealed class TrayMemory(string path)
{
    private sealed class Data
    {
        public List<string> LaterVersions { get; set; } = [];
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illux Image Tool", "tray.json");

    public static TrayMemory CreateDefault() => new(DefaultPath);

    public bool IsLater(string version) => Load().LaterVersions.Contains(version, StringComparer.OrdinalIgnoreCase);

    public void MarkLater(string version)
    {
        var data = Load();
        if (data.LaterVersions.Contains(version, StringComparer.OrdinalIgnoreCase)) return;
        data.LaterVersions.Add(version);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the popup then simply comes back at the next log-in.
        }
    }

    private Data Load()
    {
        try
        {
            if (!File.Exists(path)) return new Data();
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) is { LaterVersions: not null } data ? data : new Data();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Data();
        }
    }
}
