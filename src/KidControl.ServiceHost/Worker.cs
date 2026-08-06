using KidControl.Application.Services;
using KidControl.Infrastructure.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace KidControl.ServiceHost;

/// <summary>
/// The service heartbeat. Runs two independent loops:
///   • a 1-second timer loop that advances the session (<see cref="SessionService.ProcessTickAsync"/>);
///   • a 5-second watchdog loop that keeps the UI alive and reports night-time usage attempts.
/// Scheduled-task registration and the tamper watcher are started once, off the hot path.
/// Every loop iteration is wrapped so a transient fault is logged and the loop keeps running.
/// </summary>
[SupportedOSPlatform("windows10.0")]
public sealed class Worker(
    SessionService session,
    ProcessWatchdog processWatchdog,
    TamperDetector tamperDetector,
    TaskSchedulerManager taskSchedulerManager,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KidControl service started.");

        // Catch file deletions in the install directory as early as possible.
        try
        {
            tamperDetector.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start tamper detector.");
        }

        // Scheduled-task registration can be slow (COM/Task Scheduler); it must never
        // delay the timer loop, so fire it off on a background task.
        _ = Task.Run(() =>
        {
            try
            {
                taskSchedulerManager.EnsureTaskRegistered();
                logger.LogInformation("UI launch task registration finished.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UI launch task registration failed.");
            }
        }, stoppingToken);

        var tickLoop = RunTickLoopAsync(stoppingToken);
        var watchdogLoop = RunWatchdogLoopAsync(stoppingToken);
        await Task.WhenAll(tickLoop, watchdogLoop).ConfigureAwait(false);
    }

    private async Task RunTickLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await session.ProcessTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Timer tick failed.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunWatchdogLoopAsync(CancellationToken stoppingToken)
    {
        var wasUiRunning = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (session.IsPaused())
                {
                    // While paused the UI is torn down by the pause path itself; nothing to keep alive.
                    wasUiRunning = false;
                }
                else
                {
                    processWatchdog.EnsureUiRunning();
                    var uiRunning = processWatchdog.IsUiRunning();

                    if (session.IsNightActiveNow() && !uiRunning)
                    {
                        await session.NotifyNightUsageAttemptAsync(stoppingToken).ConfigureAwait(false);
                    }

                    // On the transition to running, push the current state so the freshly
                    // launched UI reflects reality immediately instead of waiting for the next tick.
                    if (uiRunning && !wasUiRunning)
                    {
                        await session.NotifyCurrentStateToUiAsync(stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("Session state pushed to UI after process became available.");
                    }

                    wasUiRunning = uiRunning;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watchdog check failed.");
            }

            try
            {
                await Task.Delay(WatchdogInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
