using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using KidControl.Application.Abstractions;
using KidControl.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

/// <summary>
/// <see cref="ISystemController"/> over Windows OS side effects. Every interaction is
/// best-effort and logged: a failure here must never crash the service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemController(
    IHostApplicationLifetime lifetime,
    ILogger<SystemController> logger) : ISystemController
{
    public Task ShutdownAsync(TimeSpan delay, CancellationToken ct = default)
        => RunShutdownAsync("/s", delay, ct);

    public Task RestartAsync(TimeSpan delay, CancellationToken ct = default)
        => RunShutdownAsync("/r", delay, ct);

    public void StopUi()
    {
        foreach (var process in SafeGetProcesses(KidControlNames.UiProcessName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                logger.LogInformation("Stopped UI process {ProcessId}.", process.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop UI process {ProcessId}.", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public void LaunchUi()
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
                logger.LogWarning("Failed to start schtasks for UI launch.");
                return;
            }

            DrainAndWait(process);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("UI launch task returned exit code {ExitCode}.", process.ExitCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to launch UI via scheduled task.");
        }
    }

    public void RequestServiceStop()
    {
        logger.LogInformation("Service stop requested.");
        lifetime.StopApplication();
    }

    private async Task RunShutdownAsync(string switchArg, TimeSpan delay, CancellationToken ct)
    {
        var seconds = Math.Max(0, (int)delay.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"{switchArg} /t {seconds} /f",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                logger.LogWarning("Failed to start shutdown.exe ({Switch}).", switchArg);
                return;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("shutdown.exe {Switch} exited {ExitCode}: {Err}", switchArg, process.ExitCode, stderr.Result.Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "shutdown.exe {Switch} failed.", switchArg);
        }
    }

    private void DrainAndWait(Process process)
    {
        try
        {
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed draining schtasks output.");
        }
    }

    private Process[] SafeGetProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate processes named {Name}.", name);
            return [];
        }
    }
}
