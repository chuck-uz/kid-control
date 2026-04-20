using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

[SupportedOSPlatform("windows10.0")]
public sealed class TaskSchedulerManager(ILogger<TaskSchedulerManager> logger)
{
    private const string TaskName = "KidControl.UiHost.Launch";
    private const string UiProcessName = "KidControl.UiHost.exe";

    public bool EnsureTaskRegistered()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, UiProcessName);
        if (!File.Exists(executablePath))
        {
            logger.LogWarning("Task registration skipped: UI executable not found at {Path}", executablePath);
            return false;
        }

        var arguments =
            $"/Create /TN \"{TaskName}\" /TR \"'{executablePath}'\" /SC ONLOGON /RL HIGHEST /RU INTERACTIVE /F";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(5000);
            if (process is null || process.ExitCode != 0)
            {
                logger.LogWarning(
                    "Task registration failed. ExitCode={ExitCode}. TaskName={TaskName}",
                    process?.ExitCode,
                    TaskName);
                return false;
            }

            logger.LogInformation("Task registration ensured successfully. TaskName={TaskName}", TaskName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task registration failed unexpectedly. TaskName={TaskName}", TaskName);
            return false;
        }
    }
}
