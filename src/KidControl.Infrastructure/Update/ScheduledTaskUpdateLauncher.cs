using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Launches the self-update via a one-shot SYSTEM scheduled task (<see cref="IUpdateLauncher"/>).
///
/// Why a scheduled task and not <c>Process.Start</c>: a process the service starts is its child.
/// When the updater then stops the service, the OS can tear the child down with it — so the swap
/// dies half-done and the service (already stopped, maybe already re-registered) never comes back.
/// Task Scheduler runs the action under its own service, in a separate process tree, so the
/// updater keeps running after our service stops. Every <c>schtasks</c> stream is drained
/// concurrently with the wait to avoid the classic full-pipe deadlock.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskUpdateLauncher(ILogger<ScheduledTaskUpdateLauncher> logger) : IUpdateLauncher
{
    private const string LocalSystemSid = "S-1-5-18";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(10);
    private static string TaskName => KidControlNames.UpdateTaskName;

    public void LaunchDetached(string installerExe, string sourceDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The scheduled-task updater is only available on Windows.");
        }

        if (!File.Exists(installerExe))
        {
            throw new FileNotFoundException("Installer executable not found.", installerExe);
        }

        // A stale task from an interrupted prior run would block /Create; clear it first.
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

        var tempFile = Path.Combine(Path.GetTempPath(), $"KidControl-update-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tempFile, BuildTaskXml(installerExe, sourceDir), Encoding.Unicode);

            var create = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempFile}\" /F");
            if (create.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to register the update task (exit {create.ExitCode}). {create.StdErr}");
            }

            var run = RunSchtasks($"/Run /TN \"{TaskName}\"");
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to start the update task (exit {run.ExitCode}). {run.StdErr}");
            }

            logger.LogInformation("Update task launched detached ({Task}); source {Source}.", TaskName, sourceDir);
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    /// <summary>
    /// On-demand (no trigger) task running as LocalSystem at the highest run level. A bounded
    /// <c>ExecutionTimeLimit</c> guarantees Task Scheduler reaps a hung updater rather than
    /// leaving it pinned forever.
    /// </summary>
    internal static string BuildTaskXml(string installerExe, string sourceDir)
    {
        var command = SecurityElement.Escape(installerExe);
        var arguments = SecurityElement.Escape($"/apply-update --source \"{sourceDir}\"");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>KidControl staged self-update (detached swap + rollback)</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{LocalSystemSid}</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
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

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                logger.LogWarning("schtasks timed out: {Arguments}", arguments);
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { logger.LogDebug(ex, "Kill failed."); }
            }

            Task.WaitAll(stdoutTask, stderrTask);
            return (process.ExitCode, stdoutTask.Result.Trim(), stderrTask.Result.Trim());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "schtasks invocation failed: {Arguments}", arguments);
            return (-1, string.Empty, ex.Message);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to delete temp task XML {Path}.", path); }
    }
}
