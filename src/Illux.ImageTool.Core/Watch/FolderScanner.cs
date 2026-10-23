using System.Text;
using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Options;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watch;

/// <summary>
/// Scans one watched folder (spec 7). A file is processed once it has the same size and last-write time on two
/// consecutive scans and can be opened exclusively. Originals are only ever moved (to Originals after success, to
/// Error after failure), never deleted, and only after the output has been re-opened successfully.
/// </summary>
public sealed class FolderScanner
{
    private const string WarningsLogName = "_warnings.log";
    private static readonly TimeSpan MissingFolderLogInterval = TimeSpan.FromMinutes(1);
    private static readonly string[] IgnoredExtensions = [".tmp", ".part", ".crdownload"];

    private readonly WatchedFolder _folder;
    private readonly ILogger _log;
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _busy;
    private DateTime _missingFolderLoggedUtc = DateTime.MinValue;
    private string? _lastMisconfiguration;

    public FolderScanner(WatchedFolder folder, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(log);
        _folder = folder;
        _log = log;
    }

    /// <summary>True only while a file is being processed.</summary>
    public bool Busy => _busy;

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!TryGetOptions(out var options)) return;

        if (!Directory.Exists(_folder.In))
        {
            var now = DateTime.UtcNow;
            if (now - _missingFolderLoggedUtc >= MissingFolderLogInterval)
            {
                _missingFolderLoggedUtc = now;
                _log.LogWarning("Watched folder '{Name}': In folder '{In}' does not exist; skipping.", _folder.Name, _folder.In);
            }
            return;
        }

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(_folder.In).Where(IsCandidate).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Watched folder '{Name}': cannot list '{In}': {Message}", _folder.Name, _folder.In, ex.Message);
            return;
        }

        var present = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _seen.Keys.Where(k => !present.Contains(k)).ToList()) _seen.Remove(gone);

        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var current = (info.Length, info.LastWriteTimeUtc);
            var stable = _seen.TryGetValue(path, out var previous) && previous == current;
            _seen[path] = current;
            if (!stable || !CanOpenExclusively(path)) continue;

            await ProcessFileAsync(path, options, ct).ConfigureAwait(false);
            _seen.Remove(path);
        }
    }

    /// <summary>Invalid parameters or folder layout mean the folder is skipped with nothing moved.</summary>
    private bool TryGetOptions(out ProcessingOptions options)
    {
        options = ProcessingOptions.Default;
        string? problem = null;
        try
        {
            options = ParameterParser.Parse(_folder.Parameters);
        }
        catch (ParameterException ex)
        {
            problem = ex.Message;
        }

        problem ??= LayoutProblem();
        if (problem is null)
        {
            _lastMisconfiguration = null;
            return true;
        }

        if (problem != _lastMisconfiguration)
        {
            _lastMisconfiguration = problem;
            _log.LogError("Watched folder '{Name}' is misconfigured and is skipped: {Problem}", _folder.Name, problem);
        }
        return false;
    }

    /// <summary>Out, Error and Originals inside In itself would make the watcher pick up its own results.</summary>
    private string? LayoutProblem()
    {
        if (string.IsNullOrWhiteSpace(_folder.In)) return "the In folder is not set.";

        var folders = new (string Role, string Path)[]
        {
            ("Out", _folder.Out), ("Error", _folder.Error), ("Originals", _folder.OriginalsOrDefault),
        };
        foreach (var (role, path) in folders)
        {
            if (string.IsNullOrWhiteSpace(path)) return $"the {role} folder is not set.";
            if (SamePath(path, _folder.In)) return $"the {role} folder is the same as the In folder.";
        }
        return null;
    }

    private static bool SamePath(string a, string b)
    {
        static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCandidate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)) return false;
        if (IgnoredExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ProcessFileAsync(string path, ProcessingOptions options, CancellationToken ct)
    {
        _busy = true;
        try
        {
            var fileName = Path.GetFileName(path);
            _log.LogInformation("Watched folder '{Name}': processing '{File}'.", _folder.Name, fileName);

            var result = await FileProcessor.ProcessAsync(
                path, _folder.Out, options, NamePolicy.Suffix, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct)
                .ConfigureAwait(false);

            var status = result.Status;
            var messages = result.Messages.ToList();
            if (status != Status.Error && VerifyOutputs(result.Outputs) is { } problem)
            {
                DeleteQuietly(result.Outputs);
                status = Status.Error;
                messages.Add(problem);
            }

            try
            {
                if (status == Status.Error) HandleError(path, messages);
                else HandleSuccess(path, result.Outputs, status, messages);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original stays in In and is retried on the next scan.
                _log.LogError("Watched folder '{Name}': '{File}' could not be moved: {Message}", _folder.Name, fileName, ex.Message);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void HandleSuccess(string path, IReadOnlyList<string> outputs, Status status, List<string> messages)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            MoveWithoutOverwrite(path, _folder.OriginalsOrDefault);
        }
        catch
        {
            // Without the original safely stored the outputs would be duplicated by the retry.
            DeleteQuietly(outputs);
            throw;
        }

        if (status == Status.Warning)
        {
            AppendWarnings(fileName, messages);
            _log.LogWarning("Watched folder '{Name}': '{File}' done with warnings: {Messages}", _folder.Name, fileName, string.Join("; ", messages));
        }
        else
        {
            _log.LogInformation("Watched folder '{Name}': '{File}' done.", _folder.Name, fileName);
        }
    }

    private void HandleError(string path, List<string> messages)
    {
        var fileName = Path.GetFileName(path);
        var moved = MoveWithoutOverwrite(path, _folder.Error);
        try
        {
            var text = new StringBuilder()
                .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine(fileName);
            foreach (var message in messages) text.AppendLine(message);
            var txt = UniquePath(_folder.Error, Path.GetFileName(moved) + ".error.txt");
            File.WriteAllText(txt, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError("Watched folder '{Name}': could not write the error note for '{File}': {Message}", _folder.Name, fileName, ex.Message);
        }
        _log.LogError("Watched folder '{Name}': '{File}' failed: {Messages}", _folder.Name, fileName, string.Join("; ", messages));
    }

    private void AppendWarnings(string fileName, List<string> messages)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {fileName}");
            foreach (var message in messages) text.AppendLine("  " + message);
            File.AppendAllText(Path.Combine(_folder.Out, WarningsLogName), text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Watched folder '{Name}': could not write {Log}: {Message}", _folder.Name, WarningsLogName, ex.Message);
        }
    }

    /// <summary>Re-opens every output; null means all are fine.</summary>
    private static string? VerifyOutputs(IReadOnlyList<string> outputs)
    {
        foreach (var output in outputs)
        {
            try
            {
                if (new FileInfo(output).Length == 0) return $"output '{Path.GetFileName(output)}' is empty";
                var ok = string.Equals(Path.GetExtension(output), ".pdf", StringComparison.OrdinalIgnoreCase)
                    ? LooksLikePdf(output)
                    : CanReadAsImage(output);
                if (!ok) return $"output '{Path.GetFileName(output)}' could not be re-opened";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MagickException)
            {
                return $"output '{Path.GetFileName(output)}' could not be re-opened ({ex.Message})";
            }
        }
        return null;
    }

    private static bool CanReadAsImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new MagickImageInfo(stream);
        return info.Width > 0 && info.Height > 0;
    }

    private static bool LooksLikePdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[8];
        if (stream.Read(header) != header.Length || Encoding.ASCII.GetString(header) != "%PDF-1.6") return false;

        var tailLength = (int)Math.Min(stream.Length, 1024);
        stream.Seek(-tailLength, SeekOrigin.End);
        var tail = new byte[tailLength];
        stream.ReadExactly(tail);
        return Encoding.ASCII.GetString(tail).TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal);
    }

    /// <summary>Moves a file into a folder (created if missing); on a name clash the new name is <c>name (2).ext</c>.</summary>
    private static string MoveWithoutOverwrite(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        while (true)
        {
            var target = UniquePath(folder, name);
            try
            {
                File.Move(path, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Someone created the name between the check and the move: pick the next one.
            }
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void DeleteQuietly(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
