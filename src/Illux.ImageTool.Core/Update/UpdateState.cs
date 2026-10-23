using System.Text.Json;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Update;

/// <summary>The valid values of <see cref="UpdateState.Phase"/>.</summary>
public static class UpdatePhase
{
    public const string Idle = "Idle";
    public const string Downloading = "Downloading";
    public const string WaitingForJobs = "WaitingForJobs";
    public const string Installing = "Installing";
    public const string Failed = "Failed";
}

public enum InstallOutcome { ChecksumFailed, Busy, Started, Failed }

/// <summary>What the service tells the settings window and tray helper about updates (<c>update-state.json</c>).</summary>
public sealed class UpdateState
{
    public string? AvailableVersion { get; set; }

    public DateTimeOffset? LastCheck { get; set; }

    public string Phase { get; set; } = UpdatePhase.Idle;

    public string? Error { get; set; }
}

/// <summary>Reads and writes <c>update-state.json</c> in <see cref="AppPaths.Root"/>; the UI polls it while the service writes it.</summary>
public static class UpdateStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FilePath => Path.Combine(AppPaths.Root, "update-state.json");

    /// <summary>A missing or unreadable file gives the default (idle) state; this never throws.</summary>
    public static UpdateState Load()
    {
        var path = FilePath;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new UpdateState();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<UpdateState>(stream, Json) ?? new UpdateState();
            }
            catch (JsonException)
            {
                return new UpdateState();
            }
            catch (IOException)
            {
                Thread.Sleep(25); // a writer is swapping the file in; try again
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
        return new UpdateState();
    }

    /// <summary>Writes through a temporary file so a reader never sees a half-written state.</summary>
    public static void Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
                {
                    // A reader (the settings window, a virus scanner) briefly has the target open; replacing fails then.
                    Thread.Sleep(15);
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
