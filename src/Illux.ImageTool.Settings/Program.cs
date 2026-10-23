using System.ComponentModel;

namespace Illux.ImageTool.Settings;

internal static class Program
{
    private const string TrayMutexName = @"Local\IlluxImageToolTray";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (ServiceControl.TryParseArguments(args, out var action)) return RunServiceAction(action);

        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            // One tray helper per user: a second one started at log-in exits quietly.
            using var mutex = new Mutex(initiallyOwned: true, TrayMutexName, out var first);
            if (!first) return 0;
            TrayApp.InstallExceptionHandlers();
            var memory = TrayMemory.CreateDefault();
            memory.ForgetLater(); // the tray starts at log-in
            Application.Run(new TrayApp(memory));
            return 0;
        }

        // config.json is admin-only (R34): the window runs elevated; the non-elevated copy only starts it.
        if (Elevation.ShouldSelfElevate(args, Elevation.IsElevated()))
        {
            if (!Elevation.RelaunchElevated())
                MessageBox.Show("Illux Image Tool Settings needs administrator rights to change the settings.", "Illux Image Tool",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>The elevated copy: does exactly one service action and exits.</summary>
    private static int RunServiceAction(ServiceAction action)
    {
        try
        {
            switch (action)
            {
                case ServiceAction.Start:
                    ServiceControl.Start();
                    break;
                case ServiceAction.Stop:
                    ServiceControl.Stop();
                    break;
                case ServiceAction.Account:
                    using (var dialog = new AccountDialog())
                    {
                        if (dialog.ShowDialog() != DialogResult.OK) return 1;
                        ServiceControl.ChangeAccount(dialog.User, dialog.TakePassword());
                    }
                    MessageBox.Show("The service account was changed. It is used the next time the service starts.",
                        "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
            }
            return 0;
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
        {
            SettingsLog.Write($"Service action {action} failed.", ex);
            MessageBox.Show(ex.Message, "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }
}
