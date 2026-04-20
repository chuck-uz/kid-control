using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

[SupportedOSPlatform("windows10.0")]
public sealed class ProcessWatchdog(ILogger<ProcessWatchdog> logger)
{
    private const string UiProcessName = "KidControl.UiHost";
    private const string UiFallbackTaskName = "KidControl.UiHost.Launch";

    public bool IsUiRunning()
    {
        return Process.GetProcessesByName(UiProcessName).Length > 0;
    }

    public bool EnsureUiRunning()
    {
        if (IsUiRunning())
        {
            return true;
        }

        return TryLaunchUiInActiveSession();
    }

    private bool TryLaunchUiInActiveSession()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            logger.LogWarning("No active console session found for UI launch.");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            var error = Marshal.GetLastWin32Error();
            logger.LogError("WTSQueryUserToken failed. Win32={Error}", error);
            logger.LogInformation("Trying scheduled-task fallback because user token is unavailable.");
            return TryLaunchUiViaScheduledTask();
        }

        try
        {
            if (!DuplicateTokenEx(
                    userToken,
                    TokenAccessLevels.MaximumAllowed,
                    IntPtr.Zero,
                    SecurityImpersonationLevel.SecurityImpersonation,
                    TokenType.TokenPrimary,
                    out var duplicatedToken))
            {
                logger.LogError("DuplicateTokenEx failed. Win32={Error}", Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                var executablePath = Path.Combine(AppContext.BaseDirectory, $"{UiProcessName}.exe");
                if (!File.Exists(executablePath))
                {
                    logger.LogWarning("UI executable not found at {Path}", executablePath);
                    return false;
                }

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
                    logger.LogError("CreateProcessAsUser failed. Win32={Error}", error);
                    if (error == 1314)
                    {
                        logger.LogInformation("Trying scheduled-task fallback because CreateProcessAsUser returned 1314.");
                        return TryLaunchUiViaScheduledTask();
                    }

                    return false;
                }

                CloseHandle(processInfo.hThread);
                CloseHandle(processInfo.hProcess);
                logger.LogInformation("UI process launched in user session {SessionId}.", sessionId);
                return true;
            }
            finally
            {
                CloseHandle(duplicatedToken);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    private bool TryLaunchUiViaScheduledTask()
    {
        try
        {
            logger.LogInformation("Attempting UI launch via scheduled task {TaskName}.", UiFallbackTaskName);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{UiFallbackTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(3000);
            if (process is null || process.ExitCode != 0)
            {
                logger.LogWarning(
                    "Fallback scheduled task launch failed. ExitCode={ExitCode}. TaskName={TaskName}",
                    process?.ExitCode,
                    UiFallbackTaskName);
                return false;
            }

            logger.LogInformation("UI launch fallback succeeded via scheduled task {TaskName}.", UiFallbackTaskName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallback scheduled task launch failed.");
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
