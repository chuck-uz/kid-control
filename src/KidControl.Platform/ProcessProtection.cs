using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KidControl.Platform;

/// <summary>
/// Shared Win32 self-protection: denies PROCESS_TERMINATE to non-SYSTEM principals
/// and (optionally) marks the process as critical. Previously duplicated verbatim in
/// both ServiceHost/Program.cs and UiHost/ProcessProtection.cs — now a single source.
///
/// Every method is best-effort and swallows failure: protection is defence-in-depth,
/// never a hard dependency for the app to run.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessProtection
{
    // DACL: deny terminate to Users+Admins, allow SYSTEM full, allow execute/read to Admins/Users.
    private const string DenyTerminateSddl =
        "D:(D;;0x1;;;BU)(D;;0x1;;;BA)(A;;0x1FFFFF;;;SY)(A;;GXRC;;;BA)(A;;GXRC;;;BU)";

    private const uint DaclSecurityInformation = 0x00000004;
    private const uint WriteDac = 0x00040000;

    /// <summary>Applies a DACL to the current process denying termination by non-SYSTEM callers.</summary>
    public static void DenyTerminationForNonSystem(Action<string>? log = null)
    {
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(DenyTerminateSddl, 1, out var pSd, out _))
            {
                log?.Invoke($"ProcessProtection: ConvertStringSD failed. Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                var hProcess = OpenProcess(WriteDac, false, (uint)Environment.ProcessId);
                if (hProcess == IntPtr.Zero)
                {
                    log?.Invoke($"ProcessProtection: OpenProcess failed. Win32={Marshal.GetLastWin32Error()}");
                    return;
                }

                try
                {
                    if (!SetKernelObjectSecurity(hProcess, DaclSecurityInformation, pSd))
                    {
                        log?.Invoke($"ProcessProtection: SetKernelObjectSecurity failed. Win32={Marshal.GetLastWin32Error()}");
                        return;
                    }

                    log?.Invoke("ProcessProtection: PROCESS_TERMINATE denied for Users/Admins.");
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
            log?.Invoke($"ProcessProtection: exception — {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the process marked critical, guaranteeing the flag is
    /// cleared even on an unhandled crash (fixes the original BSOD-on-crash landmine).
    /// </summary>
    public static async Task RunCriticalAsync(Func<Task> body, Action<string>? log = null)
    {
        var marked = TrySetCritical(true, log);
        void OnExit(object? s, EventArgs e) => TrySetCritical(false, null);
        if (marked)
        {
            AppDomain.CurrentDomain.ProcessExit += OnExit;
            AppDomain.CurrentDomain.UnhandledException += (_, _) => TrySetCritical(false, null);
        }

        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            if (marked)
            {
                TrySetCritical(false, log);
                AppDomain.CurrentDomain.ProcessExit -= OnExit;
            }
        }
    }

    private static bool TrySetCritical(bool critical, Action<string>? log)
    {
        try
        {
            RtlSetProcessIsCritical(critical, out _, false);
            log?.Invoke(critical ? "Process marked critical." : "Process critical flag cleared.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"RtlSetProcessIsCritical({critical}) failed: {ex.Message}");
            return false;
        }
    }

    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int RtlSetProcessIsCritical(
        [MarshalAs(UnmanagedType.Bool)] bool bNew,
        [MarshalAs(UnmanagedType.Bool)] out bool bOld,
        [MarshalAs(UnmanagedType.Bool)] bool bNeedScb);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint stringSDRevision, out IntPtr securityDescriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, uint securityInformation, IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
