using System.ComponentModel;
using System.Diagnostics;

namespace Illux.ImageTool.Jpeg;

public sealed class JpegTranException : Exception
{
    public JpegTranException(string message) : base(message) { }
    public JpegTranException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Lossless JPEG transcoding with libjpeg-turbo's jpegtran. Without <c>-optimize</c> jpegtran writes the
/// standard Huffman tables, i.e. the same result as Photoshop's "Baseline (Standard)".
/// </summary>
/// <remarks>
/// jpegtran is a C program that opens file names through the ANSI code page, so names with characters outside
/// the system code page (and paths over MAX_PATH) break. We therefore pass no paths at all: the input file is
/// streamed into its stdin and its stdout is copied into a file stream that .NET owns.
/// </remarks>
public static class JpegTran
{
    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "tools", "jpegtran", "jpegtran.exe");

    /// <summary>
    /// Rewrites <paramref name="inputPath"/> as a baseline JPEG without touching pixel data or any metadata segment.
    /// On any failure or cancellation no file is left at <paramref name="outputPath"/>.
    /// </summary>
    public static Task ToBaselineAsync(string inputPath, string outputPath, CancellationToken ct = default) =>
        ToBaselineAsync(inputPath, outputPath, ExePath, ct);

    internal static string MissingExeMessage(string exePath) =>
        $@"jpegtran.exe not found at '{exePath}'. Run build\fetch-jpegtran.ps1 and rebuild.";

    internal static async Task ToBaselineAsync(string inputPath, string outputPath, string exePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(exePath))
            throw new JpegTranException(MissingExeMessage(exePath));

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-copy");
        psi.ArgumentList.Add("all");

        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        FileStream? input = null;
        FileStream? output = null;
        var started = false;
        var success = false;
        try
        {
            input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            try
            {
                process.Start();
                started = true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new JpegTranException($"Could not start jpegtran.exe: {ex.Message}", ex);
            }

            // Any pump failure (or cancellation) kills jpegtran so that no other pump stays blocked on a pipe.
            using var killOnCancel = cts.Token.Register(() => TryKill(process));

            var feed = Guard(cts, token => FeedAsync(input, process.StandardInput, token));
            var drain = Guard(cts, token => process.StandardOutput.BaseStream.CopyToAsync(output, token));
            var errors = Guard(cts, token => process.StandardError.ReadToEndAsync(token));
            await Task.WhenAll(feed, drain, errors).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var text = errors.Result.Trim();
                throw new JpegTranException(
                    $"jpegtran failed with exit code {FormatExitCode(process.ExitCode)}: {(text.Length > 0 ? text : "no error output")}");
            }

            await output.DisposeAsync().ConfigureAwait(false);
            success = true;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            if (!success)
            {
                if (started)
                {
                    TryKill(process);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                if (output != null) await output.DisposeAsync().ConfigureAwait(false);
                TryDelete(outputPath);
            }
            if (input != null) await input.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task Guard(CancellationTokenSource cts, Func<CancellationToken, Task> pump)
    {
        try { await pump(cts.Token).ConfigureAwait(false); }
        catch { cts.Cancel(); throw; }
    }

    private static async Task<T> Guard<T>(CancellationTokenSource cts, Func<CancellationToken, Task<T>> pump)
    {
        try { return await pump(cts.Token).ConfigureAwait(false); }
        catch { cts.Cancel(); throw; }
    }

    private static async Task FeedAsync(Stream input, StreamWriter stdin, CancellationToken ct)
    {
        try
        {
            await input.CopyToAsync(stdin.BaseStream, ct).ConfigureAwait(false);
            await stdin.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            // jpegtran quit early (e.g. not a JPEG) and closed the pipe; its exit code and stderr tell the story.
        }
        finally
        {
            try { stdin.Close(); } catch (IOException) { }
        }
    }

    private static string FormatExitCode(int code)
    {
        if (code is >= 0 and <= 255) return code.ToString();
        var hex = $"{code} (0x{code:X8}";
        return code == unchecked((int)0xC0000135) || code == unchecked((int)0xC000007B)
            ? hex + ", missing runtime DLL?)"
            : hex + ")";
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
