using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

[SupportedOSPlatform("windows10.0")]
public sealed class TaskSchedulerManager(ILogger<TaskSchedulerManager> logger)
{
    private const string TaskName = "KidControl.UiHost.Launch";
    private const string UiProcessName = "KidControl.UiHost.exe";

    /// <summary>
    /// Interactive logon group — task runs in the security context of the user that logs on (not LocalSystem).
    /// </summary>
    private const string InteractiveLogonGroupSid = "S-1-5-4";

    public bool EnsureTaskRegistered()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, UiProcessName);
        if (!File.Exists(executablePath))
        {
            logger.LogWarning("Task registration skipped: UI executable not found at {Path}", executablePath);
            return false;
        }

        var tr = executablePath.Replace("\"", "\\\"", StringComparison.Ordinal);

        try
        {
            if (QueryTaskExists())
            {
                var exit = RunSchtasks($"/Change /TN \"{TaskName}\" /TR \"{tr}\"");
                if (exit == 0)
                {
                    logger.LogInformation("Scheduled task path updated. TaskName={TaskName}", TaskName);
                    return true;
                }

                logger.LogWarning(
                    "Scheduled task /Change failed. ExitCode={ExitCode}. TaskName={TaskName}. Will try XML import.",
                    exit,
                    TaskName);

                return TryCreateTaskFromXml(executablePath);
            }

            if (TryCreateTaskFromXml(executablePath))
            {
                return true;
            }

            var createExit = RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"{tr}\" /SC ONLOGON /RL HIGHEST /F");
            if (createExit == 0)
            {
                logger.LogInformation("Scheduled task created via schtasks /Create. TaskName={TaskName}", TaskName);
                return true;
            }

            logger.LogWarning(
                "Scheduled task registration failed. ExitCode={ExitCode}. TaskName={TaskName}",
                createExit,
                TaskName);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task registration failed unexpectedly. TaskName={TaskName}", TaskName);
            return false;
        }
    }

    private bool QueryTaskExists()
    {
        var exit = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return exit == 0;
    }

    private int RunSchtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        if (process is null)
        {
            return -1;
        }

        process.WaitForExit(8000);
        var err = process.StandardError.ReadToEnd().Trim();
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        if (process.ExitCode != 0 && (!string.IsNullOrEmpty(err) || !string.IsNullOrEmpty(stdout)))
        {
            logger.LogDebug("schtasks {Arguments} -> {Exit}. Out={Out} Err={Err}", arguments, process.ExitCode, stdout, err);
        }

        return process.ExitCode;
    }

    private bool TryCreateTaskFromXml(string executablePath)
    {
        var escapedPath = SecurityElement.Escape(executablePath);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>KidControl UI host at user logon</Description>
                <URI>\\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <GroupId>{InteractiveLogonGroupSid}</GroupId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedPath}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        var tempFile = Path.Combine(Path.GetTempPath(), $"KidControl-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, xml, Encoding.Unicode);
            var exit = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempFile}\" /F");
            if (exit == 0)
            {
                logger.LogInformation("Scheduled task created via XML. TaskName={TaskName}", TaskName);
                return true;
            }

            logger.LogWarning("Scheduled task XML import failed. ExitCode={ExitCode}. TaskName={TaskName}", exit, TaskName);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
