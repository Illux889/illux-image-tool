using Avalonia.Controls;

namespace Illux.ImageTool.Settings;

/// <summary>Shows read-only text: the command-line help (the Automation Engine parameters) or the service log.</summary>
public partial class HelpWindow : Window
{
    public HelpWindow() : this("")
    {
    }

    public HelpWindow(string text, string title = "AE parameter help")
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        Title = title;
        Body.Text = text.ReplaceLineEndings("\n");
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            Body.CaretIndex = 0;
            Body.Focus();
        };
    }
}
