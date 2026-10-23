using System.Runtime.InteropServices;
using Illux.ImageTool.Cli;

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the run unwind and report instead of dying mid-write
    Cancel(cts);
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    Cancel(cts);
});

return await CliApp.RunAsync(args, Console.Out, cts.Token);

static void Cancel(CancellationTokenSource source)
{
    try { source.Cancel(); }
    catch (ObjectDisposedException) { }
}
