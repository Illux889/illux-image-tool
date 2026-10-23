namespace Illux.ImageTool.Settings;

/// <summary>Asks for the service account in the elevated copy; the password never travels on a command line.</summary>
internal sealed class AccountDialog : Form
{
    private readonly TextBox _user = new() { Dock = DockStyle.Fill, PlaceholderText = @"DOMAIN\user or user" };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

    public AccountDialog()
    {
        Text = "Service account";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

        var hint = new Label { Text = Hint, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 8) };
        table.Controls.Add(hint, 0, 0);
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(new Label { Text = "User", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        table.Controls.Add(_user, 1, 1);
        table.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        table.Controls.Add(_password, 1, 2);

        var ok = new Button { Text = "OK", Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) =>
        {
            if (ServiceControl.NeedsPassword(ServiceControl.BuildAccountName(User)) && _password.Text.Length == 0)
            {
                MessageBox.Show(this, "Enter the password for this account.", "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var account = ServiceControl.BuildAccountName(User);
            if (!ServiceControl.IsBuiltInAccount(account) && LocalAdministrators.IsDirectMember(account) != true
                && MessageBox.Show(this, NotAdminWarning(account), "Illux Image Tool", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        table.Controls.Add(buttons, 0, 3);
        table.SetColumnSpan(buttons, 2);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>R43: updates are installed by the service, which needs administrator rights for that.</summary>
    internal const string Hint =
        "The watcher runs as this account. Leave the user empty for the Local System account. A user ending in $ (managed service account) needs no password.\r\n" +
        "Use an account that can reach your network folders. It must be a local Administrator (or a managed service account in the local Administrators group): otherwise the service cannot install updates.";

    internal static string NotAdminWarning(string account) =>
        $"{account} is not listed in the local Administrators group of this computer (or this could not be checked).\r\n\r\n" +
        "Without administrator rights the service cannot install updates: \"Update now\" will fail. " +
        "If the account is an administrator through a domain group, this is fine.\r\n\r\nUse this account anyway?";

    public string User => _user.Text.Trim();

    /// <summary>The typed password; the box is cleared so it does not linger on screen or in the control.</summary>
    public string TakePassword()
    {
        var password = _password.Text;
        _password.Clear();
        return password;
    }
}
