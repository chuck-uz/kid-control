using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

/// <summary>
/// Keeps the UI process alive in the interactive desktop session. The service runs in
/// session 0, so it duplicates the console user's token and launches the UI with
/// <c>CreateProcessAsUser</c>; if that is unavailable (e.g. Win32 error 1314,
/// "a required privilege is not held"), it falls back to triggering the logon scheduled task.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessWatchdog(ILogger<ProcessWatchdog> logger)
{
    private const uint NoActiveSession = 0xFFFFFFFF;
    private const int ErrorPrivilegeNotHeld = 1314;

    public bool IsUiRunning()
    {
        try
        {
            return Process.GetProcessesByName(KidControlNames.UiProcessName).Length > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query UI process state.");
            return false;
        }
    }

    public bool EnsureUiRunning() => IsUiRunning() || TryLaunchInActiveSession();

    private bool TryLaunchInActiveSession()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoActiveSession)
        {
            logger.LogWarning("No active console session; cannot launch UI.");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            logger.LogWarning("WTSQueryUserToken failed (Win32={Error}); trying scheduled task.", Marshal.GetLastWin32Error());
            return TryLaunchViaScheduledTask();
        }

        try
        {
            return LaunchAsUser(userToken, sessionId);
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private bool LaunchAsUser(IntPtr userToken, uint sessionId)
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, KidControlNames.UiExecutableName);
        if (!File.Exists(executablePath))
        {
            logger.LogWarning("UI executable not found at {Path}.", executablePath);
            return false;
        }

        if (!DuplicateTokenEx(
                userToken,
                TokenAccessLevels.MaximumAllowed,
                IntPtr.Zero,
                SecurityImpersonationLevel.SecurityImpersonation,
                TokenType.TokenPrimary,
                out var duplicatedToken))
        {
            logger.LogWarning("DuplicateTokenEx failed (Win32={Error}).", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            if (!CreateProcessAsUser(
                    duplicatedToken,
                    executablePath,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    0,
                    IntPtr.Zero,
                    Path.GetDirectoryName(executablePath),
                    ref startupInfo,
                    out var processInfo))
            {
                var error = Marshal.GetLastWin32Error();
                logger.LogWarning("CreateProcessAsUser failed (Win32={Error}).", error);
                return error == ErrorPrivilegeNotHeld && TryLaunchViaScheduledTask();
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            logger.LogInformation("UI launched in session {SessionId}.", sessionId);
            return true;
        }
        finally
        {
            CloseHandle(duplicatedToken);
        }
    }

    private bool TryLaunchViaScheduledTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{KidControlNames.UiLaunchTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(3000);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Scheduled-task UI launch failed (exit {ExitCode}).", process.ExitCode);
                return false;
            }

            logger.LogInformation("UI launched via scheduled task.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled-task UI launch failed.");
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingTokenHandle,
        TokenAccessLevels desiredAccess,
        IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out IntPtr duplicateTokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum SecurityImpersonationLevel
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum TokenType
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
