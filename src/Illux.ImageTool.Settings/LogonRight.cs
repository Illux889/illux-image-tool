using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Illux.ImageTool.Settings;

/// <summary>Grants an account the "Log on as a service" right (SeServiceLogonRight). Needs admin rights.</summary>
internal static class LogonRight
{
    private const string ServiceLogonRight = "SeServiceLogonRight";
    private const uint PolicyLookupNames = 0x00000800;
    private const uint PolicyCreateAccount = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;

    public static void Grant(string account)
    {
        var sid = LookupSid(account);

        var attributes = new LsaObjectAttributes { Length = (uint)Marshal.SizeOf<LsaObjectAttributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyLookupNames | PolicyCreateAccount, out var policy);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        try
        {
            var right = new LsaUnicodeString
            {
                Buffer = ServiceLogonRight,
                Length = (ushort)(ServiceLogonRight.Length * 2),
                MaximumLength = (ushort)((ServiceLogonRight.Length + 1) * 2),
            };
            status = LsaAddAccountRights(policy, sid, [right], 1);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        }
        finally { LsaClose(policy); }
    }

    private static byte[] LookupSid(string account)
    {
        // ".\bob" means the local machine; LookupAccountName wants the machine name.
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;

        uint sidSize = 0, domainSize = 0;
        LookupAccountNameW(null, name, null, ref sidSize, null, ref domainSize, out _);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) throw new Win32Exception(Marshal.GetLastWin32Error());

        var sid = new byte[sidSize];
        var domain = new StringBuilder((int)domainSize);
        if (!LookupAccountNameW(null, name, sid, ref sidSize, domain, ref domainSize, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return sid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Buffer;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountNameW(
        string? systemName, string accountName, byte[]? sid, ref uint sidSize, StringBuilder? domain, ref uint domainSize, out int use);

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] accountSid, LsaUnicodeString[] userRights, uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);
}
