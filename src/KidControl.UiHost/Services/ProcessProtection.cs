using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace KidControl.UiHost.Services;

[SupportedOSPlatform("windows")]
public static class ProcessProtection
{
    // DACL rules:
    //   (D;;0x1;;;BU)       - DENY  PROCESS_TERMINATE for BUILTIN\Users
    //   (D;;0x1;;;BA)       - DENY  PROCESS_TERMINATE for BUILTIN\Administrators
    //   (A;;0x1FFFFF;;;SY)  - ALLOW full process access for SYSTEM
    //   (A;;GXRC;;;BA)      - ALLOW generic execute + read-control for Admins (query, monitor)
    //   (A;;GXRC;;;BU)      - ALLOW generic execute + read-control for Users  (query, monitor)
    private const string ProtectSddl =
        "D:(D;;0x1;;;BU)(D;;0x1;;;BA)(A;;0x1FFFFF;;;SY)(A;;GXRC;;;BA)(A;;GXRC;;;BU)";

    private const uint DaclSecurityInformation = 0x00000004;
    private const uint SddlRevision1 = 1;
    private const uint WriteDac = 0x00040000;

    public static void Apply()
    {
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    ProtectSddl, SddlRevision1,
                    out var pSd, out _))
            {
                Log.Warning("ProcessProtection: ConvertStringSecurityDescriptor failed. Win32={Error}",
                    Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                // GetCurrentProcess() returns a pseudo-handle that SetKernelObjectSecurity
                // silently rejects when modifying the DACL. A real handle opened with
                // WRITE_DAC is required for the security change to take effect.
                var hProcess = OpenProcess(WriteDac, false, (uint)Environment.ProcessId);
                if (hProcess == IntPtr.Zero)
                {
                    Log.Warning("ProcessProtection: OpenProcess failed. Win32={Error}",
                        Marshal.GetLastWin32Error());
                    return;
                }

                try
                {
                    if (!SetKernelObjectSecurity(hProcess, DaclSecurityInformation, pSd))
                    {
                        Log.Warning("ProcessProtection: SetKernelObjectSecurity failed. Win32={Error}",
                            Marshal.GetLastWin32Error());
                        return;
                    }

                    Log.Information("ProcessProtection: DACL applied — PROCESS_TERMINATE denied for Users/Admins.");
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            finally
            {
                LocalFree(pSd);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ProcessProtection: failed to apply DACL.");
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(
        IntPtr handle,
        uint securityInformation,
        IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
