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
public static class JpegTran
{
    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "tools", "jpegtran", "jpegtran.exe");

    /// <summary>Rewrites <paramref name="inputPath"/> as a baseline JPEG without touching pixel data or any metadata segment.</summary>
    public static async Task ToBaselineAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(ExePath))
            throw new JpegTranException(
                $"jpegtran.exe not found at '{ExePath}'. Run build\fetch-jpegtran.ps1 and rebuild.");

        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-copy");
        psi.ArgumentList.Add("all");
        psi.ArgumentList.Add("-outfile");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add(inputPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new JpegTranException($"Could not start jpegtran.exe: {ex.Message}", ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            TryDelete(outputPath);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            var text = (await stderr.ConfigureAwait(false)).Trim();
            if (text.Length == 0) text = (await stdout.ConfigureAwait(false)).Trim();
            throw new JpegTranException(
                $"jpegtran failed with exit code {process.ExitCode}: {(text.Length > 0 ? text : "no error output")}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
