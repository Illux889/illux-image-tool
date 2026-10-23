using System.Diagnostics;
using ImageMagick;

namespace Illux.ImageTool.Tests;

/// <summary>Runs the real wrapper scripts (scripts/ae) against the built CLI, the way Esko's Script Runner would.</summary>
[Collection("AppRoot")]
public sealed class AeScriptTests : AppRootTestBase
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "IlluxImageTool.exe");

    // A folder with a space, because AE paths contain spaces and the scripts must keep them intact.
    private string In => Path.Combine(Dir, "in put");

    private string Out => Path.Combine(Dir, "out put");

    private string UpdatingFlag => Path.Combine(AppRoot, "updating.flag");

    private static string Script(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IlluxImageTool.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "scripts", "ae", name);
    }

    private static (int Exit, string Output) Run(string script, string[] args, params (string Name, string Value)[] env) =>
        Run(script, args, quoteAll: false, env);

    /// <param name="quoteAll">Passes every argument in double quotes, the way AE may do it. Otherwise only arguments with spaces are quoted.</param>
    private static (int Exit, string Output) Run(string script, string[] args, bool quoteAll, params (string Name, string Value)[] env)
    {
        var isPs1 = script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isPs1 ? "powershell.exe" : script)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isPs1)
        {
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
        }
        if (quoteAll)
        {
            psi.Arguments = string.Join(" ", args.Select(a => $"\"{a}\""));
        }
        else
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.Environment["ILLUX_IMAGETOOL_EXE"] = Exe;
        foreach (var (name, value) in env) psi.Environment[name] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "script did not finish in 120 s");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string Tiff16(string name) => TestImages.Write(In, name, 60, 40, ColorSpace.sRGB, 16, 100);

    public static TheoryData<string> Scripts => new() { "IlluxImageTool.bat", "IlluxImageTool.ps1" };

    private void AssertForwardsArgsAndExitCode(string name)
    {
        var a = TestImages.Write(In, "a.jpg", 60, 40, ColorSpace.sRGB, 8, 200);

        var good = Run(Script(name), [a, Out]);
        var bad = Run(Script(name), [a, Out, "bogus"]);

        Assert.Equal(0, good.Exit);
        Assert.Contains("Result: OK (1 files)", good.Output);
        Assert.True(File.Exists(Path.Combine(Out, "a.jpg")));
        Assert.Equal(2, bad.Exit);
        Assert.Contains("ERROR: Unknown parameter 'bogus'.", bad.Output);
    }

    [Fact]
    public void Bat_ForwardsArgsAndExitCode()
    {
        AssertForwardsArgsAndExitCode("IlluxImageTool.bat");
    }

    [Fact]
    public void Ps1_ForwardsArgsAndExitCode()
    {
        AssertForwardsArgsAndExitCode("IlluxImageTool.ps1");
    }

    [Fact]
    public void Bat_TwelveParams_AllForwarded()
    {
        var a = Tiff16("a.tif");
        string[] args = [a, Out, .. Enumerable.Repeat("", 11), "8bit"];

        var (exit, output) = Run(Script("IlluxImageTool.bat"), args);

        Assert.Equal(0, exit);
        using var result = new MagickImage(Path.Combine(Out, "a.tif"));
        Assert.Equal(8u, result.Depth);
        Assert.Equal(60u, result.Width); // 8bit only: the 100 ppi image was not upsampled
        Assert.Contains("Result: OK", output);
    }

    [Fact]
    public async Task Bat_WaitsForUpdatingFlag()
    {
        var a = Tiff16("a.tif");
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(UpdatingFlag, "");
        var remover = Task.Run(async () => { await Task.Delay(2000); File.Delete(UpdatingFlag); });

        var (exit, output) = Run(Script("IlluxImageTool.bat"), [a, Out], ("ILLUX_IMAGETOOL_WAIT_SECONDS", "1"));
        await remover;

        Assert.Equal(0, exit);
        Assert.Contains("Result: OK", output);
        Assert.True(File.Exists(Path.Combine(Out, "a.tif")));
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_UpdateNeverFinishes_Returns2(string name)
    {
        var a = Tiff16("a.tif");
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(UpdatingFlag, "");

        var (exit, output) = Run(Script(name), [a, Out],
            ("ILLUX_IMAGETOOL_WAIT_SECONDS", "1"), ("ILLUX_IMAGETOOL_WAIT_MAX_SECONDS", "2"));

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: update did not finish", output);
        Assert.False(Directory.Exists(Out));
    }

    [Fact]
    public async Task Ps1_WaitsForUpdatingFlag()
    {
        var a = Tiff16("a.tif");
        Directory.CreateDirectory(AppRoot);
        File.WriteAllText(UpdatingFlag, "");
        var remover = Task.Run(async () => { await Task.Delay(2000); File.Delete(UpdatingFlag); });

        var (exit, output) = Run(Script("IlluxImageTool.ps1"), [a, Out], ("ILLUX_IMAGETOOL_WAIT_SECONDS", "1"));
        await remover;

        Assert.Equal(0, exit);
        Assert.Contains("Result: OK", output);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_ExeMissing_Returns2(string name)
    {
        var a = Tiff16("a.tif");

        var (exit, output) = Run(Script(name), [a, Out], ("ILLUX_IMAGETOOL_EXE", Path.Combine(Dir, "nope.exe")));

        Assert.Equal(2, exit);
        Assert.Contains("ERROR: ", output);
        Assert.Contains("nope.exe", output);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_OutputFolderWithTrailingBackslash_Works(string name)
    {
        var a = Tiff16("a.tif");

        var (exit, _) = Run(Script(name), [a, Out + "\\"]);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(Out, "a.tif")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bat_NameValueParams_AreNotSplitByCmd(bool quoted)
    {
        var a = Tiff16("a.tif");

        var (exit, output) = Run(Script("IlluxImageTool.bat"), [a, Out, "output=pdf"], quoted);

        Assert.Equal(0, exit);
        Assert.Contains("Result: OK", output);
        Assert.Equal(["a.pdf"], Directory.GetFileSystemEntries(Out).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bat_UpsampleWithPpi_IsKept(bool quoted)
    {
        var a = Tiff16("a.tif");

        var (exit, _) = Run(Script("IlluxImageTool.bat"), [a, Out, "8bit", "upsample=300"], quoted);

        Assert.Equal(0, exit);
        using var result = new MagickImage(Path.Combine(Out, "a.tif"));
        Assert.Equal(8u, result.Depth);
        Assert.Equal(180u, result.Width); // 60 px at 100 ppi -> 300 ppi
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bat_NameValueParams_WithOtherParamBetween(bool quoted)
    {
        var a = Tiff16("a.tif");

        var (exit, _) = Run(Script("IlluxImageTool.bat"), [a, Out, "output=pdf", "8bit", "upsample=300"], quoted);

        Assert.Equal(0, exit);
        Assert.Equal(["a.pdf"], Directory.GetFileSystemEntries(Out).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public void Bat_UpsampleWithoutPpi_DoesNotSwallowNextParam()
    {
        var a = Tiff16("a.tif");

        var (exit, _) = Run(Script("IlluxImageTool.bat"), [a, Out, "upsample", "8bit"]);

        Assert.Equal(0, exit);
        using var result = new MagickImage(Path.Combine(Out, "a.tif"));
        Assert.Equal(8u, result.Depth);
        Assert.Equal(120u, result.Width); // default 200 ppi
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_ExeCannotStart_Returns2(string name)
    {
        var a = Tiff16("a.tif");
        var broken = Path.Combine(Dir, "bad.exe");
        File.WriteAllText(broken, "this is not a program");

        var (exit, _) = Run(Script(name), [a, Out], ("ILLUX_IMAGETOOL_EXE", broken));

        Assert.Equal(2, exit);
    }

    [Theory]
    [MemberData(nameof(ScriptsWithExitCodes))]
    public void Script_ExitCodeOtherThan0Or1_BecomesError(string name, int exeExit, int expected)
    {
        var a = Tiff16("a.tif");

        var (exit, _) = Run(Script(name), [a, Out], ("ILLUX_IMAGETOOL_EXE", FakeExe()), ("FAKE_EXIT", exeExit.ToString()));

        Assert.Equal(expected, exit);
    }

    public static TheoryData<string, int, int> ScriptsWithExitCodes()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var script in new[] { "IlluxImageTool.bat", "IlluxImageTool.ps1" })
        {
            data.Add(script, 0, 0);
            data.Add(script, 1, 1);
            data.Add(script, 2, 2);
            data.Add(script, 3, 2);
            data.Add(script, 216, 2);
            data.Add(script, -1073741515, 2); // 0xC0000135: a DLL is missing
        }
        return data;
    }

    /// <summary>A tiny console program that exits with the code in <c>FAKE_EXIT</c>; compiled with Windows PowerShell's built-in C# compiler.</summary>
    private string FakeExe()
    {
        var path = Path.Combine(Dir, "fake.exe");
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(Dir);
        const string Source = "public static class P { public static int Main() { return int.Parse(System.Environment.GetEnvironmentVariable(\"FAKE_EXIT\")); } }";
        var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-Command", $"Add-Type -TypeDefinition '{Source}' -OutputAssembly '{path}' -OutputType ConsoleApplication" }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(File.Exists(path), "could not build the fake exe: " + output);
        return path;
    }
}
