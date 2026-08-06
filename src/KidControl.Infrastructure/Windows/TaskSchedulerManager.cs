using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

/// <summary>
/// Registers/updates the scheduled task that launches the UI at interactive logon.
///
/// Fix over the original: <see cref="Process"/> output is drained with async reads that are
/// awaited alongside <see cref="Process.WaitForExit()"/>, so a chatty <c>schtasks</c> can
/// never fill the pipe buffer and deadlock (the original read both streams sequentially
/// only after the process had exited).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TaskSchedulerManager(ILogger<TaskSchedulerManager> logger)
{
    private const string InteractiveLogonGroupSid = "S-1-5-4";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(8);

    private static string TaskName => KidControlNames.UiLaunchTaskName;

    public bool EnsureTaskRegistered()
    {
        var executablePath = Path.Combine(AppContext.BaseDirectory, KidControlNames.UiExecutableName);
        if (!File.Exists(executablePath))
        {
            logger.LogWarning("Task registration skipped: UI executable not found at {Path}.", executablePath);
            return false;
        }

        try
        {
            if (TaskExists())
            {
                var tr = executablePath.Replace("\"", "\\\"", StringComparison.Ordinal);
                if (RunSchtasks($"/Change /TN \"{TaskName}\" /TR \"{tr}\"").ExitCode == 0)
                {
                    logger.LogInformation("Scheduled task path updated. TaskName={TaskName}", TaskName);
                    return true;
                }

                logger.LogWarning("Scheduled task /Change failed; falling back to XML import.");
            }

            return TryCreateFromXml(executablePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task registration failed unexpectedly. TaskName={TaskName}", TaskName);
            return false;
        }
    }

    private bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    private bool TryCreateFromXml(string executablePath)
    {
        var escapedPath = SecurityElement.Escape(executablePath);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>KidControl UI host at user logon</Description>
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
            if (RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempFile}\" /F").ExitCode == 0)
            {
                logger.LogInformation("Scheduled task created via XML. TaskName={TaskName}", TaskName);
                return true;
            }

            logger.LogWarning("Scheduled task XML import failed. TaskName={TaskName}", TaskName);
            return false;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunSchtasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return (-1, string.Empty, string.Empty);
            }

            // Start draining BOTH streams before waiting — prevents a full-pipe deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                logger.LogWarning("schtasks timed out: {Arguments}", arguments);
                TryKill(process);
            }

            Task.WaitAll(stdoutTask, stderrTask);
            var stdout = stdoutTask.Result.Trim();
            var stderr = stderrTask.Result.Trim();

            if (process.ExitCode != 0 && (stdout.Length > 0 || stderr.Length > 0))
            {
                logger.LogDebug("schtasks {Arguments} -> {Exit}. Out={Out} Err={Err}", arguments, process.ExitCode, stdout, stderr);
            }

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "schtasks invocation failed: {Arguments}", arguments);
            return (-1, string.Empty, string.Empty);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to kill timed-out schtasks process.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete temp task XML {Path}.", path);
        }
    }
}
