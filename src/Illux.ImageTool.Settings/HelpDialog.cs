namespace Illux.ImageTool.Settings;

/// <summary>Shows read-only text: the command-line help (the Automation Engine parameters) or the service log.</summary>
internal sealed class HelpDialog : Form
{
    public HelpDialog(string text, string title = "AE parameter help")
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 520);
        MinimumSize = new Size(400, 300);
        ShowInTaskbar = false;

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            Text = text.ReplaceLineEndings("\r\n"),
            BackColor = SystemColors.Window,
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Dock = DockStyle.Bottom, Height = 32 };
        Controls.Add(box);
        Controls.Add(close);
        CancelButton = close;
        Shown += (_, _) => box.Select(0, 0);
    }
}
