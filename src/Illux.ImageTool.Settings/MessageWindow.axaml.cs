using Avalonia.Controls;

namespace Illux.ImageTool.Settings;

/// <summary>
/// A short message ("Illux Image Tool") with OK, or a question with Yes and No (No is the default). Avalonia has no
/// message box of its own.
/// </summary>
public partial class MessageWindow : Window
{
    private bool _yes;

    public MessageWindow() : this("", question: false)
    {
    }

    private MessageWindow(string text, bool question)
    {
        InitializeComponent();
        Icon = AppIcon.Get();
        MessageText.Text = text;
        if (question)
        {
            OkButton.IsVisible = OkButton.IsDefault = OkButton.IsCancel = false;
            YesButton.IsVisible = NoButton.IsVisible = true;
            NoButton.IsDefault = NoButton.IsCancel = true; // the safe answer, as Enter and Escape
        }
        OkButton.Click += (_, _) => Close();
        NoButton.Click += (_, _) => Close();
        YesButton.Click += (_, _) =>
        {
            _yes = true;
            Close();
        };
    }

    /// <summary>Shows <paramref name="text"/> with OK; modal on <paramref name="owner"/> when it is open.</summary>
    public static Task ShowAsync(Window? owner, string text) => ShowCoreAsync(owner, new MessageWindow(text, question: false));

    /// <summary>Asks <paramref name="text"/> with Yes and No; true for Yes.</summary>
    public static async Task<bool> AskAsync(Window? owner, string text)
    {
        var window = new MessageWindow(text, question: true);
        await ShowCoreAsync(owner, window);
        return window._yes;
    }

    private static Task ShowCoreAsync(Window? owner, MessageWindow window)
    {
        if (owner is { IsVisible: true }) return window.ShowDialog(owner);

        // No window to belong to (the tray, or before the main window): a free-standing window in the middle of the screen.
        var closed = new TaskCompletionSource();
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowInTaskbar = true;
        window.Topmost = true;
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Activate();
        return closed.Task;
    }
}
