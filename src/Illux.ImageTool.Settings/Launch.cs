namespace Illux.ImageTool.Settings;

internal enum LaunchKind
{
    /// <summary>The settings window.</summary>
    Window,

    /// <summary>The tray (Windows) or menu-bar (macOS) helper, <c>--tray</c>.</summary>
    Tray,

    /// <summary>Windows only: the elevated copy asking for the service account, <c>--service account</c>.</summary>
    Account,

    /// <summary>Only a message, then the process ends with <see cref="Launch.ExitCode"/>.</summary>
    Message,
}

/// <summary>What this process shows once Avalonia has started.</summary>
internal sealed record Launch(LaunchKind Kind, string Message = "", int ExitCode = 0);
