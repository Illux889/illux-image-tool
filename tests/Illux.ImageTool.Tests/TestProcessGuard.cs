using System.Runtime.CompilerServices;
using Illux.ImageTool.Runtime;

namespace Illux.ImageTool.Tests;

/// <summary>
/// Keeps the whole test process (and every child process it starts, such as the CLI or the AE wrapper scripts)
/// away from the real <c>C:\ProgramData\Illux Image Tool</c> folder.
/// </summary>
/// <remarks>
/// The module initializer runs before any test code and sets <c>ILLUX_IMAGETOOL_ROOT</c> in the process environment,
/// which child processes inherit. <see cref="AppRootTestBase"/> and other per-test overrides may set their own value;
/// when they restore "the previous value" it is this safe one, never null.
/// </remarks>
internal static class TestProcessGuard
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "illux-tests-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, true); } catch (Exception) { /* best effort; it is a temp folder */ }
        };
    }
}
