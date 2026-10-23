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
/// A file that keeps failing (for example because Originals is unreachable) is parked and retried with a growing
/// delay instead of being reprocessed on every poll.
/// </summary>
public sealed class FolderScanner
{
    private const string WarningsLogName = "_warnings.log";
    private const int MaxMoveAttempts = 1000;
    private static readonly TimeSpan MissingFolderLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LockedLogAfter = TimeSpan.FromMinutes(5);
    private static readonly string[] IgnoredExtensions = [".tmp", ".part", ".crdownload"];

    private readonly WatchedFolder _folder;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Parked> _parked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LockedFile> _locked = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _busy;
    private DateTime _missingFolderLoggedUtc = DateTime.MinValue;
    private string? _lastMisconfiguration;

    private readonly record struct Parked(long Length, DateTime LastWriteUtc, int Failures, DateTime NextRetryUtc);

    private sealed class LockedFile(DateTime since)
    {
        public DateTime Since { get; } = since;

        public bool Logged { get; set; }
    }

    public FolderScanner(WatchedFolder folder, ILogger log, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(log);
        _folder = folder;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True only while a file is being processed.</summary>
    public bool Busy => _busy;

    /// <summary>Re-open check for the outputs (null = fine, otherwise the reason). Replaceable so tests can force a failure.</summary>
    internal Func<IReadOnlyList<string>, string?> OutputVerifier { get; set; } = VerifyOutputs;

    private DateTime NowUtc => _clock.GetUtcNow().UtcDateTime;

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        if (!TryGetOptions(out var options)) return;

        if (!Directory.Exists(_folder.In))
        {
            var now = NowUtc;
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
        foreach (var gone in _parked.Keys.Where(k => !present.Contains(k)).ToList()) _parked.Remove(gone);
        foreach (var gone in _locked.Keys.Where(k => !present.Contains(k)).ToList()) _locked.Remove(gone);

        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();

            (long Length, DateTime LastWriteUtc) current;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                current = (info.Length, info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var stable = _seen.TryGetValue(path, out var previous) && previous == current;
            _seen[path] = current;
            if (!stable) continue;

            if (_parked.TryGetValue(path, out var parked))
            {
                if (parked.Length != current.Length || parked.LastWriteUtc != current.LastWriteUtc)
                    _parked.Remove(path); // the file changed: give it a fresh start
                else if (NowUtc < parked.NextRetryUtc)
                    continue;
            }

            if (!CanOpenExclusively(path))
            {
                NoteLocked(path);
                continue;
            }
            _locked.Remove(path);

            try
            {
                await ProcessFileAsync(path, options, ct).ConfigureAwait(false);
                _parked.Remove(path);
                _seen.Remove(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One failing file must not starve the others; the original is still in In.
                Park(path, current, ex.Message);
            }
        }
    }

    /// <summary>First failure at Error, later ones at Debug; the retry delay doubles from 1 to 30 minutes.</summary>
    private void Park(string path, (long Length, DateTime LastWriteUtc) signature, string reason)
    {
        var failures = _parked.TryGetValue(path, out var existing) && existing.Length == signature.Length
            && existing.LastWriteUtc == signature.LastWriteUtc ? existing.Failures + 1 : 1;
        var delay = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << Math.Min(failures - 1, 10)));
        _parked[path] = new Parked(signature.Length, signature.LastWriteUtc, failures, NowUtc + delay);

        var name = Path.GetFileName(path);
        if (failures == 1)
            _log.LogError("Watched folder '{Name}': '{File}' could not be handled and stays in the In folder; retrying in {Delay}: {Reason}", _folder.Name, name, delay, reason);
        else
            _log.LogDebug("Watched folder '{Name}': '{File}' failed again ({Failures}x); retrying in {Delay}: {Reason}", _folder.Name, name, failures, delay, reason);
    }

    /// <summary>A stable file that stays exclusively locked is logged once, after five minutes.</summary>
    private void NoteLocked(string path)
    {
        var now = NowUtc;
        if (!_locked.TryGetValue(path, out var locked)) _locked[path] = locked = new LockedFile(now);
        if (locked.Logged || now - locked.Since < LockedLogAfter) return;

        locked.Logged = true;
        _log.LogWarning("Watched folder '{Name}': '{File}' has been locked by another program for more than {Minutes} minutes and is not processed yet.",
            _folder.Name, Path.GetFileName(path), (int)LockedLogAfter.TotalMinutes);
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

    /// <summary>
    /// All four folders must be absolute (a service has no meaningful current directory). Out, Error and Originals
    /// inside In itself would make the watcher pick up its own results, and Out must not double as Originals.
    /// </summary>
    private string? LayoutProblem()
    {
        var folders = new (string Role, string Path)[]
        {
            ("In", _folder.In), ("Out", _folder.Out), ("Error", _folder.Error), ("Originals", _folder.OriginalsOrDefault),
        };
        foreach (var (role, path) in folders)
        {
            if (string.IsNullOrWhiteSpace(path)) return $"the {role} folder is not set.";
            if (!Path.IsPathFullyQualified(path)) return $"the {role} folder '{path}' is not a full path (for example C:\\Folder or \\\\server\\share\\Folder).";
        }

        foreach (var (role, path) in folders.Skip(1))
        {
            if (SamePath(path, _folder.In)) return $"the {role} folder is the same as the In folder.";
        }
        return SamePath(_folder.Out, _folder.OriginalsOrDefault) ? "the Out folder is the same as the Originals folder." : null;
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

    /// <summary>Throws when the original could not be put away (it is then still in In and the outputs are removed).</summary>
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

            try
            {
                var status = result.Status;
                var messages = result.Messages.ToList();
                if (status != Status.Error && OutputVerifier(result.Outputs) is { } problem)
                {
                    DeleteQuietly(result.Outputs);
                    status = Status.Error;
                    messages.Add(problem);
                }

                if (status == Status.Error) HandleError(path, messages);
                else HandleSuccess(path, status, messages);
            }
            catch
            {
                // Without the original safely stored the outputs would be duplicated by the retry.
                DeleteQuietly(result.Outputs);
                throw;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void HandleSuccess(string path, Status status, List<string> messages)
    {
        var fileName = Path.GetFileName(path);
        MoveWithoutOverwrite(path, _folder.OriginalsOrDefault);

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
                .AppendLine(_clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss"))
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
            text.AppendLine($"{_clock.GetLocalNow():yyyy-MM-dd HH:mm:ss} {fileName}");
            foreach (var message in messages) text.AppendLine("  " + message);
            File.AppendAllText(Path.Combine(_folder.Out, WarningsLogName), text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) // best effort: the original is already safe, so this must never undo the outputs
        {
            _log.LogWarning("Watched folder '{Name}': could not write {Log}: {Message}", _folder.Name, WarningsLogName, ex.Message);
        }
    }

    /// <summary>Re-opens every output; null means all are fine.</summary>
    internal static string? VerifyOutputs(IReadOnlyList<string> outputs)
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

    /// <summary>
    /// Moves a file into a folder (created if missing); on a name clash the new name is <c>name (2).ext</c>.
    /// Throws if the file is still at its old place afterwards.
    /// </summary>
    private static string MoveWithoutOverwrite(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        for (var attempt = 0; attempt < MaxMoveAttempts; attempt++)
        {
            var target = UniquePath(folder, name);
            try
            {
                File.Move(path, target, overwrite: false);
            }
            catch (IOException ex) when (IsAlreadyExists(ex))
            {
                continue; // someone took the name between the check and the move: pick the next one
            }

            if (File.Exists(path)) throw new IOException($"'{name}' is still in its old folder after the move.");
            return target;
        }
        throw new IOException($"No free name for '{name}' in '{folder}' after {MaxMoveAttempts} attempts.");
    }

    /// <summary>ERROR_FILE_EXISTS (80) and ERROR_ALREADY_EXISTS (183).</summary>
    private static bool IsAlreadyExists(IOException ex) => (ex.HResult & 0xFFFF) is 80 or 183;

    /// <summary>A directory with the same name counts as taken.</summary>
    private static string UniquePath(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){extension}");
            if (!Path.Exists(candidate)) return candidate;
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
