using System.Text;
using ImageMagick;
using Illux.ImageTool.Batch;
using Illux.ImageTool.Options;
using Illux.ImageTool.Runtime;
using Microsoft.Extensions.Logging;

namespace Illux.ImageTool.Watch;

/// <summary>
/// Scans one watched folder (spec 7). A file is processed once it has the same size and last-write time on two
/// consecutive scans and can be opened exclusively. Originals are only ever moved (to Originals after success, to
/// Error after failure), never deleted, and only after the output has been re-opened successfully.
/// A file that keeps failing (for example because Originals is unreachable) is parked and retried with a growing
/// delay instead of being reprocessed on every poll. While an update is pending (<c>updating.flag</c>) no new file is
/// started (R42).
/// On macOS the scanner runs in the folders job as the folder account (V15). There, only regular files in In are taken
/// (no link, FIFO or device; I1), the probe for a lock never blocks, <c>_warnings.log</c> is only appended to when it is
/// this account's own file with one name (C1), and each file is staged in the account's private work folder (C2).
/// File names go into every log line without control characters (both systems).
/// </summary>
public sealed class FolderScanner
{
    private const string WarningsLogName = "_warnings.log";
    private const int MaxMoveAttempts = 1000;
    private static readonly TimeSpan MissingFolderLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LockedLogAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);
    private static readonly string[] IgnoredExtensions = [".tmp", ".part", ".crdownload"];
    private static int _staleTempCleanupNoticeLogged;

    private readonly WatchedFolder _folder;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _isUpdating;
    private readonly Func<IReadOnlyList<ResolutionRule>> _rules;
    private readonly Func<IDisposable?>? _runMarker;
    private readonly string? _stagingFolder;
    private readonly HashSet<string> _skippedLogged = new(StringComparer.Ordinal);
    private bool _pausedLogged;
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

    /// <param name="isUpdating">True while an update is pending; defaults to "<see cref="AppPaths.UpdatingFlag"/> exists".</param>
    /// <param name="rules">The resolution rules to use, asked for on every scan; defaults to <see cref="ResolutionRules.Defaults"/>.</param>
    /// <param name="runMarker">
    /// Taken before <c>updating.flag</c> is looked at and held while a file is processed; for the macOS folders job
    /// (V15), whose updater runs in another process and only sees it through a lock in <c>runs/</c> (as the CLI's, R30).
    /// Null from it means it could not be taken; the file is processed anyway, as in the CLI.
    /// </param>
    /// <param name="stagingFolder">
    /// macOS folders job (C2): the account's private work folder, in which each file is staged instead of a temporary
    /// folder in Out (see <see cref="FileProcessor"/>). Null: the temporary folder in Out (Windows, R46).
    /// </param>
    public FolderScanner(
        WatchedFolder folder, ILogger log, TimeProvider? clock = null, Func<bool>? isUpdating = null,
        Func<IReadOnlyList<ResolutionRule>>? rules = null, Func<IDisposable?>? runMarker = null, string? stagingFolder = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(log);
        _folder = folder;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _isUpdating = isUpdating ?? (() => File.Exists(AppPaths.UpdatingFlag));
        _rules = rules ?? (() => ResolutionRules.Defaults);
        _runMarker = runMarker;
        _stagingFolder = stagingFolder;
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
        if (_skippedLogged.Count > 0) _skippedLogged.RemoveWhere(p => !File.Exists(p) && !Directory.Exists(p) && !LinkGuard.IsLink(p));
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

            // Busy (and the run marker) is claimed before the flag is looked at: the updater writes the flag and then
            // checks Busy and the run locks again, so either it sees this file being processed or this scanner sees its
            // flag (the CLI does the same, R30).
            _busy = true;
            using var run = _runMarker?.Invoke();
            if (IsUpdatePending())
            {
                _busy = false;
                return;
            }

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

    private bool IsUpdatePending()
    {
        bool updating;
        try
        {
            updating = _isUpdating();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            updating = true; // cannot tell: wait rather than risk being cut off by the installer
        }

        if (!updating)
        {
            _pausedLogged = false;
            return false;
        }
        if (!_pausedLogged)
        {
            _pausedLogged = true;
            _log.LogInformation("Watched folder '{Name}': an update is being installed; new files wait until it is finished.", _folder.Name);
        }
        return true;
    }

    /// <summary>
    /// Removes temporary output folders (<c>.illux-tmp-*</c>) older than an hour from Out: a service that was killed
    /// in the middle of a file (for example by an installer after its time-out) leaves one behind. Only a folder the tool
    /// made itself (owner and protected ACL, see <see cref="TempFolder"/>) is removed, and no link is followed (R46);
    /// anything else is logged and left for an administrator. Best effort.
    /// On macOS nothing in Out is ever removed (no recursive deletes in folders others may write to). The folders job
    /// (with a staging folder, V15) removes old per-file folders from its private staging folder instead; without one
    /// that is logged once per process at Information.
    /// </summary>
    public void RemoveStaleTempFolders()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (_stagingFolder is not null)
            {
                try
                {
                    var removed = FileProcessor.RemoveStaleStaging(_stagingFolder, NowUtc, StaleTempAge);
                    if (removed > 0) _log.LogInformation("Removed {Count} unfinished staging folder(s) from '{Staging}'.", removed, _stagingFolder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogDebug("Old staging folders in '{Staging}' not checked: {Message}", _stagingFolder, ex.Message);
                }
                return;
            }
            if (Interlocked.Exchange(ref _staleTempCleanupNoticeLogged, 1) == 0)
                _log.LogInformation("Unfinished temporary folders ({Prefix}*) in Out are not removed automatically on macOS; an administrator may remove old ones.", FileProcessor.TempPrefix);
            return;
        }
        try
        {
            var outFolder = new DirectoryInfo(_folder.Out);
            if (!outFolder.Exists || outFolder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            var now = NowUtc;
            foreach (var temp in outFolder.EnumerateDirectories(FileProcessor.TempPrefix + "*"))
            {
                var latest = temp.LastWriteTimeUtc > temp.CreationTimeUtc ? temp.LastWriteTimeUtc : temp.CreationTimeUtc;
                if (now - latest < StaleTempAge) continue;
                try
                {
                    if (TempFolder.WhyNotOurs(temp) is { } reason)
                    {
                        _log.LogWarning("Watched folder '{Name}': left the old temporary folder '{Temp}' alone because {Reason}; an administrator may remove it.", _folder.Name, temp.FullName, reason);
                        continue;
                    }
                    TempFolder.DeleteTree(temp.FullName);
                    _log.LogInformation("Watched folder '{Name}': removed the unfinished temporary folder '{Temp}'.", _folder.Name, temp.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning("Watched folder '{Name}': could not remove '{Temp}': {Message}", _folder.Name, temp.FullName, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogDebug("Watched folder '{Name}': temporary folders in Out not checked: {Message}", _folder.Name, ex.Message);
        }
    }

    /// <summary>First failure at Error, later ones at Debug; the retry delay doubles from 1 to 30 minutes.</summary>
    private void Park(string path, (long Length, DateTime LastWriteUtc) signature, string reason)
    {
        var failures = _parked.TryGetValue(path, out var existing) && existing.Length == signature.Length
            && existing.LastWriteUtc == signature.LastWriteUtc ? existing.Failures + 1 : 1;
        var delay = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << Math.Min(failures - 1, 10)));
        _parked[path] = new Parked(signature.Length, signature.LastWriteUtc, failures, NowUtc + delay);

        var name = LogText.Clean(Path.GetFileName(path));
        reason = LogText.Clean(reason);
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
            _folder.Name, LogText.Clean(Path.GetFileName(path)), (int)LockedLogAfter.TotalMinutes);
    }

    /// <summary>Invalid parameters or folder layout mean the folder is skipped with nothing moved.</summary>
    private bool TryGetOptions(out ProcessingOptions options)
    {
        options = ProcessingOptions.Default;
        string? problem = null;
        try
        {
            options = ParameterParser.Parse(_folder.Parameters) with { Rules = _rules() };
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

    private bool IsCandidate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)) return false;
        if (IgnoredExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            if (UnixNative.IsSupported && !IsRegularFile(path)) return false;
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// macOS (I1): only a regular file that is not a link is taken (lstat); a link, FIFO, socket or device is skipped
    /// silently, said once at Debug.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private bool IsRegularFile(string path)
    {
        if (UnixNative.LStat(path) is { Type: UnixNative.RegularFileType }) return true;
        if (_skippedLogged.Add(path))
            _log.LogDebug("Watched folder '{Name}': '{File}' is not a regular file and is skipped.", _folder.Name, LogText.Clean(Path.GetFileName(path)));
        return false;
    }

    private static bool CanOpenExclusively(string path)
    {
        // macOS: never blocks (O_NONBLOCK) and never follows a link, unlike an open by .NET (I1).
        if (UnixNative.IsSupported) return UnixNative.CanLockExclusively(path);
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
            var fileName = LogText.Clean(Path.GetFileName(path));
            _log.LogInformation("Watched folder '{Name}': processing '{File}'.", _folder.Name, fileName);

            var result = await FileProcessor.ProcessAsync(
                path, _folder.Out, options, NamePolicy.Suffix, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct,
                _stagingFolder).ConfigureAwait(false);

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
        var fileName = LogText.Clean(Path.GetFileName(path));
        messages = messages.Select(LogText.Clean).ToList();
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
        var fileName = LogText.Clean(Path.GetFileName(path));
        messages = messages.Select(LogText.Clean).ToList();
        var moved = MoveWithoutOverwrite(path, _folder.Error);
        try
        {
            var text = new StringBuilder()
                .AppendLine(_clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine(fileName);
            foreach (var message in messages) text.AppendLine(message);
            var txt = UniquePath(_folder.Error, Path.GetFileName(moved) + ".error.txt");
            using var note = new FileStream(txt, FileMode.CreateNew, FileAccess.Write, FileShare.Read); // never through an existing link (R44)
            note.Write(new UTF8Encoding(false).GetBytes(text.ToString()));
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
            var log = Path.Combine(_folder.Out, WarningsLogName);
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
            if (UnixNative.IsSupported)
            {
                // C1: never through a link or a second name, and only into this account's own file.
                if (UnixNative.AppendToOwnFile(log, bytes) is { } refused)
                    _log.LogWarning("Watched folder '{Name}': could not write {Log}: {Reason}; it is left alone.", _folder.Name, WarningsLogName, refused);
                return;
            }
            LinkGuard.ThrowIfLinkedFile(log); // R44
            using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
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

    // Image outputs are PDF 1.6; a PDF input keeps its own version (never lowered), so any version can come out.
    private static readonly HashSet<string> PdfVersions = new(StringComparer.Ordinal)
    {
        "%PDF-1.0", "%PDF-1.1", "%PDF-1.2", "%PDF-1.3", "%PDF-1.4", "%PDF-1.5", "%PDF-1.6", "%PDF-1.7", "%PDF-2.0",
    };

    private static bool LooksLikePdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[8];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
            || !PdfVersions.Contains(Encoding.ASCII.GetString(header))) return false;

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
            catch (IOException ex) when (IsAlreadyExists(ex, target))
            {
                continue; // someone took the name between the check and the move: pick the next one
            }

            if (File.Exists(path)) throw new IOException($"'{name}' is still in its old folder after the move.");
            return target;
        }
        throw new IOException($"No free name for '{name}' in '{folder}' after {MaxMoveAttempts} attempts.");
    }

    /// <summary>
    /// M5: the move failed because <paramref name="target"/> is taken. Windows: ERROR_FILE_EXISTS (80) or
    /// ERROR_ALREADY_EXISTS (183). Unix: EEXIST (17), which .NET passes on as the HResult; and on every system, as a
    /// fallback that does not depend on the error code, an entry (also a link) now has that name.
    /// </summary>
    internal static bool IsAlreadyExists(IOException ex, string target)
    {
        if (OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 80 or 183 : ex.HResult == 17) return true;
        try
        {
            return Path.Exists(target) || LinkGuard.IsLink(target);
        }
        catch (Exception check) when (check is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A directory, or a link (even one that points nowhere), with the same name counts as taken.</summary>
    private static string UniquePath(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){extension}");
            if (!Path.Exists(candidate) && !LinkGuard.IsLink(candidate)) return candidate;
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
