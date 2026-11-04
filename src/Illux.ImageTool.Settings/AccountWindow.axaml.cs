using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Illux.ImageTool.Settings;

/// <summary>
/// Asks for the service account in the elevated copy (<c>--service account</c>, Windows only); the password never
/// travels on a command line.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class AccountWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();

    public AccountWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        HintText.Text = AccountText.Hint;
        OkButton.Click += async (_, _) => await TryAcceptAsync();
        CancelButton.Click += (_, _) => Close();
        Opened += (_, _) => UserBox.Focus();
        Closed += (_, _) => _answer.TrySetResult(false);
    }

    /// <summary>True once OK was pressed and the input passed the checks; false when the window was closed otherwise.</summary>
    public Task<bool> Answer => _answer.Task;

    public string User => (UserBox.Text ?? "").Trim();

    /// <summary>The typed password; the box is cleared so it does not linger on screen or in the control.</summary>
    public string TakePassword()
    {
        var password = PasswordBox.Text ?? "";
        PasswordBox.Text = "";
        return password;
    }

    private async Task TryAcceptAsync()
    {
        var account = ServiceControl.BuildAccountName(User);
        if (ServiceControl.NeedsPassword(account) && string.IsNullOrEmpty(PasswordBox.Text))
        {
            await MessageWindow.ShowAsync(this, "Enter the password for this account.");
            return;
        }
        if (!ServiceControl.IsBuiltInAccount(account) && LocalAdministrators.IsDirectMember(account) != true
            && !await MessageWindow.AskAsync(this, AccountText.NotAdminWarning(account)))
            return;
        _answer.TrySetResult(true);
        Close();
    }
}
