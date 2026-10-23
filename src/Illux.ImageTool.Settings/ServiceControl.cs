using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Illux.ImageTool.Settings;

public enum ServiceAction { Start, Stop, Account }

/// <summary>
/// The watcher service from the settings window. Reading the status needs no rights; start, stop and the account change
/// run in a second copy of this exe started with <c>runas</c> (<c>--service start|stop|account</c>).
/// </summary>
internal static class ServiceControl
{
    public const string ServiceName = "IlluxImageToolWatcher";

    public const string ServiceArgument = "--service";

    /// <summary><c>bob</c> becomes <c>.\bob</c>; <c>DOM\bob</c> and <c>bob@dom</c> stay; empty means <c>LocalSystem</c>.</summary>
    public static string BuildAccountName(string? user)
    {
        var name = user?.Trim() ?? "";
        if (name.Length == 0 || name.Equals(LocalSystem, StringComparison.OrdinalIgnoreCase)) return LocalSystem;
        return name.Contains('\\') || name.Contains('@') ? name : @".\" + name;
    }

    private const string LocalSystem = "LocalSystem";

    /// <summary>Accounts Windows manages itself: no password, and they already have the "Log on as a service" right.</summary>
    public static bool IsBuiltInAccount(string account) =>
        account.Equals(LocalSystem, StringComparison.OrdinalIgnoreCase)
        || account.Equals(@"NT AUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase)
        || account.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase);

    /// <summary>Built-in accounts and group managed service accounts (name ends with <c>$</c>) have no password to enter.</summary>
    public static bool NeedsPassword(string account) => !IsBuiltInAccount(account) && !account.EndsWith('$');

    /// <summary>Every account except the built-in ones needs the "Log on as a service" right granted.</summary>
    public static bool NeedsLogonRight(string account) => !IsBuiltInAccount(account);

    /// <summary>The command line of the elevated copy. The account password is never part of it; that copy asks for it itself.</summary>
    public static string BuildElevatedArguments(ServiceAction action) => $"{ServiceArgument} {action.ToString().ToLowerInvariant()}";

    public static bool TryParseArguments(string[] args, out ServiceAction action)
    {
        action = default;
        return args.Length == 2
            && string.Equals(args[0], ServiceArgument, StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse(args[1], ignoreCase: true, out action)
            && Enum.IsDefined(action);
    }

    public static ProcessStartInfo BuildElevatedStartInfo(string exePath, ServiceAction action) => new(exePath, BuildElevatedArguments(action))
    {
        UseShellExecute = true,
        Verb = "runas",
    };

    /// <summary>Starts the elevated copy. False when the user declined the Windows prompt.</summary>
    public static bool RunElevated(ServiceAction action)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The program path is unknown.");
        try
        {
            using var process = Process.Start(BuildElevatedStartInfo(exe, action));
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    public static string DescribeState(int state) => state switch
    {
        NotInstalled => "Not installed",
        1 => "Stopped",
        2 => "Starting...",
        3 => "Stopping...",
        4 => "Running",
        5 => "Continuing...",
        6 => "Pausing...",
        7 => "Paused",
        _ => "Unknown",
    };

    /// <summary>The current state as text; never throws.</summary>
    public static string GetStatusText() => DescribeState(GetState());

    public static int GetState()
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return Unknown;
        try
        {
            var service = OpenServiceW(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? NotInstalled : Unknown;
            try
            {
                return QueryServiceStatus(service, out var status) ? (int)status.CurrentState : Unknown;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>Needs admin rights: call it from the elevated copy.</summary>
    public static void Start()
    {
        WithService(ServiceStart | ServiceQueryStatus, service =>
        {
            if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorServiceAlreadyRunning)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            WaitFor(service, 4);
        });
    }

    /// <summary>Needs admin rights: call it from the elevated copy.</summary>
    public static void Stop()
    {
        WithService(ServiceStop | ServiceQueryStatus, service =>
        {
            if (!ControlService(service, ServiceControlStop, out _) && Marshal.GetLastWin32Error() != ErrorServiceNotActive)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            WaitFor(service, 1);
        });
    }

    /// <summary>Needs admin rights. An empty <paramref name="user"/> means LocalSystem. Takes effect when the service next starts.</summary>
    public static void ChangeAccount(string? user, string? password)
    {
        var account = BuildAccountName(user);
        var withPassword = NeedsPassword(account);
        WithService(ServiceChangeConfig, service =>
        {
            if (!ChangeServiceConfigW(service, ServiceNoChange, ServiceNoChange, ServiceNoChange, null, null, IntPtr.Zero, null,
                    account, withPassword ? password ?? "" : null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        });

        if (!NeedsLogonRight(account)) return;
        try
        {
            LogonRight.Grant(account);
        }
        catch (Win32Exception ex)
        {
            // Shown to the user by the caller: without the right the service would not start.
            throw new InvalidOperationException(
                $"The account was changed, but Windows would not give it the \"Log on as a service\" right ({ex.Message}). Grant it in Local Security Policy, or the service will not start.", ex);
        }
    }

    private static void WithService(uint access, Action<IntPtr> action)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, ServiceName, access);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { action(service); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void WaitFor(IntPtr service, uint wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (QueryServiceStatus(service, out var status) && status.CurrentState == wanted) return;
            Thread.Sleep(250);
        }
        throw new TimeoutException("The service did not reach the expected state within 30 seconds.");
    }

    private const int NotInstalled = 0;
    private const int Unknown = -1;
    private const int ErrorCancelled = 1223;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceNoChange = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPathName, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);
}
