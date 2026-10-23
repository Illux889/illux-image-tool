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

        var hint = new Label { Text = "The watcher runs as this account. Leave the user empty for the Local System account.\r\nUse an account that can reach your network folders.", AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 8) };
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
            if (User.Length > 0 && Password.Length == 0)
            {
                MessageBox.Show(this, "Enter the password for this account.", "Illux Image Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
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

    public string User => _user.Text.Trim();

    public string Password => _password.Text;
}
