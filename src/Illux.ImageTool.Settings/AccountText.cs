namespace Illux.ImageTool.Settings;

/// <summary>The texts of <see cref="AccountWindow"/> (Windows only: there is no service account to choose on macOS).</summary>
internal static class AccountText
{
    /// <summary>R43: updates are installed by the service, which needs administrator rights for that.</summary>
    internal const string Hint =
        "The watcher runs as this account. Leave the user empty for the Local System account. A user ending in $ (managed service account) needs no password.\n" +
        "Use an account that can reach your network folders. It must be a local Administrator (or a managed service account in the local Administrators group): otherwise the service cannot install updates.";

    internal static string NotAdminWarning(string account) =>
        $"{account} is not listed in the local Administrators group of this computer (or this could not be checked).\n\n" +
        "Without administrator rights the service cannot install updates: \"Update now\" will fail. " +
        "If the account is an administrator through a domain group, this is fine.\n\nUse this account anyway?";
}
