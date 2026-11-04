using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Illux.ImageTool.Runtime;

/// <summary>
/// "Check for updates now" and "Update now" travel from the settings window and tray helper to the watcher service as
/// named kernel events (R37), not as files: the service never opens, reads or deletes a path a standard user controls.
/// The service creates the events with a DACL that lets interactive users only set (signal) them; they are
/// auto-reset, so each click is consumed exactly once and nothing is left behind when the service is not running.
/// An instance (<see cref="Open"/>) is the service's <see cref="IServiceSignals"/> on Windows.
/// </summary>
public sealed class ServiceSignals : IServiceSignals
{
    public const string CheckNowName = @"Global\IlluxImageTool.CheckNow";

    public const string InstallNowName = @"Global\IlluxImageTool.InstallNow";

    /// <summary>SYSTEM and Administrators full control; Interactive Users SYNCHRONIZE | EVENT_MODIFY_STATE; not inherited.</summary>
    public const string Sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x100002;;;IU)";

    private const uint EventModifyState = 0x0002;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorAlreadyExists = 183;
    private const int SeKernelObject = 6;
    private const uint OwnerSecurityInformation = 1;

    private readonly WaitHandle? _checkNow;
    private readonly WaitHandle? _installNow;

    private ServiceSignals(WaitHandle? checkNow, WaitHandle? installNow)
    {
        _checkNow = checkNow;
        _installNow = installNow;
    }

    /// <summary>
    /// For the service: creates both events (<see cref="Create"/>). One that cannot be created is logged and never
    /// signals; the other still works and periodic update checks still run.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ServiceSignals Open(ILogger log, string checkNowName = CheckNowName, string installNowName = InstallNowName) =>
        new(TryCreate(checkNowName, log), TryCreate(installNowName, log));

    /// <inheritdoc/>
    /// <remarks>An event that could not be created returns false at once.</remarks>
    public bool TryWaitCheckNow(TimeSpan timeout) => _checkNow?.WaitOne(timeout) == true;

    /// <inheritdoc/>
    /// <remarks>An event that could not be created returns false at once.</remarks>
    public bool TryWaitInstallNow(TimeSpan timeout) => _installNow?.WaitOne(timeout) == true;

    public void Dispose()
    {
        _checkNow?.Dispose();
        _installNow?.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private static WaitHandle? TryCreate(string name, ILogger log)
    {
        try
        {
            return Create(name);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not create the request event '{Name}'; the settings window and tray cannot reach the service", name);
            return null;
        }
    }

    /// <summary>Creates (or, after a quick service restart, reopens) an auto-reset event with <see cref="Sddl"/>; for the service.</summary>
    /// <exception cref="UnauthorizedAccessException">The event exists and was not created by SYSTEM or Administrators.</exception>
    /// <exception cref="Win32Exception">The event could not be created.</exception>
    [SupportedOSPlatform("windows")]
    public static WaitHandle Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out var descriptor, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            var handle = CreateEventW(ref attributes, manualReset: false, initialState: false, name);
            var error = Marshal.GetLastPInvokeError();
            if (handle.IsInvalid)
            {
                handle.Dispose();
                if (error == ErrorAccessDenied) // it exists already, with a DACL that does not let us use it
                    throw new UnauthorizedAccessException($"The event '{name}' already exists and cannot be used.", new Win32Exception(error));
                throw new Win32Exception(error);
            }
            // Anyone may create a named event while the service is stopped; an existing one is used only when SYSTEM or
            // Administrators own it (then it carries our DACL), never one a user planted with a DACL of their choosing.
            if (error == ErrorAlreadyExists && !IsOwnedBySystemOrAdministrators(handle))
            {
                handle.Dispose();
                throw new UnauthorizedAccessException($"The event '{name}' already exists and is not owned by SYSTEM or Administrators.");
            }
            return new NativeEvent(handle);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>Signals the service's event; for the settings window and tray helper.</summary>
    /// <exception cref="IOException">The service is not running (the event does not exist).</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not signal the service.</exception>
    [SupportedOSPlatform("windows")]
    public static void Signal(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        using var handle = OpenEventW(EventModifyState, inheritHandle: false, name);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            throw error switch
            {
                ErrorFileNotFound => new IOException(ServiceRequests.NotRunningMessage),
                ErrorAccessDenied => new UnauthorizedAccessException(ServiceRequests.NotAllowedMessage),
                _ => new IOException(new Win32Exception(error).Message),
            };
        }
        if (!SetEvent(handle)) throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnedBySystemOrAdministrators(SafeWaitHandle handle)
    {
        if (GetSecurityInfo(handle, SeKernelObject, OwnerSecurityInformation, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor) != 0)
            return false;
        try
        {
            var sid = new System.Security.Principal.SecurityIdentifier(owner);
            return sid.Value is "S-1-5-18" or "S-1-5-32-544";
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private sealed class NativeEvent : WaitHandle
    {
        public NativeEvent(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, IntPtr descriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeWaitHandle handle, int objectType, uint securityInfo, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl,
        out IntPtr descriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(
        ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(SafeWaitHandle handle);
}
