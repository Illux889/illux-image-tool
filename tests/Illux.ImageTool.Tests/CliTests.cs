using System.Reflection;
using System.Text;
using ImageMagick;
using Illux.ImageTool.Cli;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Tests;

[Collection("AppRoot")]
public sealed class CliTests : AppRootTestBase
{
    private string In => Path.Combine(Dir, "in");

    private string Out => Path.Combine(Dir, "out");

    private string Jpeg(string name, string? folder = null) =>
        TestImages.Write(folder ?? In, name, 60, 40, ColorSpace.sRGB, 8, 200);

    private static async Task<(int Exit, string Output)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var exit = await CliApp.RunAsync(args, stdout);
        return (exit, stdout.ToString());
    }

    private string[] OutNames() =>
        Directory.Exists(Out) ? Directory.GetFileSystemEntries(Out).Select(p => Path.GetFileName(p)).Order().ToArray() : [];

    [Fact]
    public async Task Cli_Ae_ProcessesAndReturnsZero()
    {
        var a = Jpeg("a.jpg");
        var b = Jpeg("b.jpg");

        var (exit, output) = await Run("ae", $"{a};{b}", Out);

        Assert.Equal(0, exit);
        Assert.Contains("Result: OK (2 files)", output);
        Assert.Equal(["a.jpg", "b.jpg"], OutNames());
        Assert.False(RunLock.AnyActive());
    }

    [Fact]
    public async Task Cli_Ae_PassesParametersOn()
    {
        var a = Jpeg("a.jpg");

        var (exit, _) = await Run("ae", a, Out, "output=pdf");

        Assert.Equal(0, exit);
        Assert.Equal(["a.pdf"], OutNames());
    }

    [Fact]
    public async Task Cli_Ae_BadParam_Returns2WithMessage()
    {
        var a = Jpeg("a.jpg");

        var (exit, output) = await Run("ae", a, Out, "bogus");

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: Unknown parameter 'bogus'.", output);
        Assert.DoesNotContain("   at ", output);
        Assert.Empty(OutNames());
    }

    [Fact]
    public async Task Cli_Ae_MissingArguments_Returns2()
    {
        var (exit, output) = await Run("ae", "only-one-argument");

        Assert.Equal(2, exit);
        Assert.StartsWith("ERROR: ", output);
    }

    [Fact]
    public async Task Cli_Ae_FailedFile_Returns2_AndOthersStillWritten()
    {
        var good = Jpeg("good.jpg");
        var bad = Path.Combine(In, "bad.jpg");
        File.WriteAllBytes(bad, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        var (exit, output) = await Run("ae", $"{bad};{good}", Out);

        Assert.Equal(2, exit);
        Assert.Contains("Result: Error (2 files)", output);
        Assert.Equal(["good.jpg"], OutNames());
    }

    [Fact]
    public async Task Cli_Run_Folder()
    {
        Jpeg("a.jpg");
        Jpeg("b.jpg");
        var hidden = Jpeg("hidden.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Jpeg("nested.jpg", Path.Combine(In, "sub"));

        var (exit, output) = await Run("run", In, "--out", Out);

        Assert.Equal(0, exit);
        Assert.Contains("Result: OK (2 files)", output);
        Assert.Equal(["a.jpg", "b.jpg"], OutNames());
    }

    [Fact]
    public async Task Cli_Run_SingleFile_WithParamsBeforeOut()
    {
        var a = Jpeg("a.jpg");

        var (exit, _) = await Run("run", a, "output=pdf", "--out", Out);

        Assert.Equal(0, exit);
        Assert.Equal(["a.pdf"], OutNames());
    }

    [Fact]
    public async Task Cli_Run_WithoutOut_Returns2()
    {
        var a = Jpeg("a.jpg");

        var (exit, output) = await Run("run", a);

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: ", output);
        Assert.Contains("--out", output);
    }

    [Fact]
    public async Task Cli_Run_MissingInput_Returns2()
    {
        var (exit, output) = await Run("run", Path.Combine(In, "nope.jpg"), "--out", Out);

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: ", output);
    }

    [Fact]
    public async Task Cli_Version_PrintsVersion()
    {
        var expected = typeof(CliApp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

        var (exit, output) = await Run("--version");

        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, output.Trim());
        Assert.DoesNotContain('+', output);
    }

    [Fact]
    public async Task Cli_Help_PrintsParameterReference()
    {
        var (exit, output) = await Run("--help");

        Assert.Equal(0, exit);
        foreach (var token in new[] { "8bit", "upsample", "baseline", "output=same", "missingppi=", "quality=", "filter=", "IlluxImageTool.exe ae", "IlluxImageTool.exe run" })
        {
            Assert.Contains(token, output);
        }
        Assert.Equal(CliHelp.Text.TrimEnd(), output.TrimEnd());
    }

    [Fact]
    public async Task Cli_NoArguments_Returns2()
    {
        var (exit, output) = await Run();

        Assert.Equal(2, exit);
        Assert.StartsWith("ERROR: ", output);
    }

    [Fact]
    public async Task Cli_UnknownCommand_Returns2()
    {
        var (exit, output) = await Run("frobnicate");

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: ", output);
    }

    [Fact]
    public async Task Cli_Cancelled_Returns2WithMessage()
    {
        var a = Jpeg("a.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stdout = new StringWriter();

        var exit = await CliApp.RunAsync(["ae", a, Out], stdout, cts.Token);

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: cancelled", stdout.ToString());
        Assert.False(RunLock.AnyActive());
    }

    [Fact]
    public async Task Cli_UnexpectedException_ShortMessageOnStdout_FullDetailInLogFile()
    {
        var a = Jpeg("a.jpg");
        var stdout = new ThrowingWriter();

        var exit = await CliApp.RunAsync(["ae", a, Out], stdout);

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: boom from the log writer", stdout.Captured.ToString());
        Assert.DoesNotContain("   at ", stdout.Captured.ToString());
        var log = File.ReadAllText(Path.Combine(AppPaths.LogsDir, $"cli-{DateTime.Now:yyyyMMdd}.log"));
        Assert.Contains("boom from the log writer", log);
        Assert.Contains("   at ", log);
    }

    [Fact]
    public async Task Cli_LogFileFailure_DoesNotChangeResult()
    {
        var a = Jpeg("a.jpg");
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(AppPaths.LogsDir, "a file where the logs folder should be");
        var stdout = new ThrowingWriter();

        var exit = await CliApp.RunAsync(["ae", a, Out], stdout);

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: boom from the log writer", stdout.Captured.ToString());
    }

    /// <summary>Throws on the <c>Result:</c> line to simulate an unexpected failure inside a run.</summary>
    private sealed class ThrowingWriter : TextWriter
    {
        public StringWriter Captured { get; } = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => Captured.Write(value);

        public override void Write(string? value) => Captured.Write(value);

        public override void WriteLine(string? value)
        {
            if (value is not null && value.StartsWith("Result:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom from the log writer");
            }
            Captured.WriteLine(value);
        }
    }
}
