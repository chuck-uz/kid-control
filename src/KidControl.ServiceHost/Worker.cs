using KidControl.Application.Services;
using KidControl.Infrastructure.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace KidControl.ServiceHost;

[SupportedOSPlatform("windows10.0")]
public sealed class Worker(
    SessionOrchestrator orchestrator,
    ProcessWatchdog processWatchdog,
    TaskSchedulerManager taskSchedulerManager,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KidControl service started.");
        try
        {
            taskSchedulerManager.EnsureTaskRegistered();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Task scheduler registration check failed.");
        }

        var lastWatchdogCheck = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!orchestrator.IsPaused())
                {
                    await orchestrator.ProcessTickAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process session tick.");
            }

            if (DateTimeOffset.UtcNow - lastWatchdogCheck >= TimeSpan.FromSeconds(5))
            {
                lastWatchdogCheck = DateTimeOffset.UtcNow;
                try
                {
                    if (orchestrator.IsPaused())
                    {
                        orchestrator.EnsureUiStopped();
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (orchestrator.IsNightModeActiveNow() && !processWatchdog.IsUiRunning())
                    {
                        await orchestrator.NotifyNightUsageAttemptAsync().ConfigureAwait(false);
                    }

                    var started = processWatchdog.EnsureUiRunning();
                    if (!started)
                    {
                        logger.LogWarning("Watchdog check finished: UI is not running.");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Watchdog check failed.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
