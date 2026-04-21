using KidControl.Application.Services;
using KidControl.Infrastructure.Configuration;
using KidControl.Infrastructure.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace KidControl.ServiceHost;

[SupportedOSPlatform("windows10.0")]
public sealed class Worker(
    SessionOrchestrator orchestrator,
    IndependentTimer independentTimer,
    ProcessWatchdog processWatchdog,
    TaskSchedulerManager taskSchedulerManager,
    IOptions<TelegramConfig> telegramConfig,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly TelegramConfig _telegramConfig = telegramConfig.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("KidControl service started.");
            DebugFlightRecorder.Log("Service starting...");
            DebugFlightRecorder.Log("Orchestrator created.");
            var startupState = orchestrator.GetCurrentState();
            DebugFlightRecorder.Log($"Current Status: {startupState.Status}");
            DebugFlightRecorder.Log($"ProgramData: {Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}");

            // Start timer first with highest priority.
            var timerTask = Task.Run(() => independentTimer.RunAsync(stoppingToken), stoppingToken);
            var watchdogTask = Task.Run(() => RunWatchdogLoopAsync(stoppingToken), stoppingToken);

            // Task registration must never block timer startup.
            _ = Task.Run(() =>
            {
                try
                {
                    taskSchedulerManager.EnsureTaskRegistered();
                    DebugFlightRecorder.Log("Task scheduler registration finished.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Task scheduler registration check failed.");
                    DebugFlightRecorder.Log($"Task scheduler registration ERROR: {ex.Message}");
                }
            }, stoppingToken);

            // Fire-and-forget Telegram initialization diagnostics. Never block timer startup.
            _ = Task.Run(() => InitializeTelegramAsync(stoppingToken), stoppingToken);

            await Task.WhenAll(timerTask, watchdogTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker ExecuteAsync failed.");
            DebugFlightRecorder.Log($"FATAL: {ex}");
        }
    }

    private async Task RunWatchdogLoopAsync(CancellationToken stoppingToken)
    {
        var wasUiRunning = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (orchestrator.IsPaused())
                {
                    orchestrator.EnsureUiStopped();
                    wasUiRunning = false;
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (orchestrator.IsNightModeActiveNow() && !processWatchdog.IsUiRunning())
                {
                    await orchestrator.NotifyNightUsageAttemptAsync().ConfigureAwait(false);
                }

                processWatchdog.EnsureUiRunning();
                var uiRunning = processWatchdog.IsUiRunning();

                if (uiRunning && !wasUiRunning)
                {
                    try
                    {
                        await orchestrator.NotifyCurrentStateToUiAsync().ConfigureAwait(false);
                        logger.LogInformation("Session state pushed to UI after process became available.");
                    }
                    catch (Exception pushEx)
                    {
                        logger.LogWarning(pushEx, "Failed to push session state to UI after launch.");
                    }
                }

                wasUiRunning = uiRunning;
                if (!uiRunning)
                {
                    logger.LogWarning("Watchdog check finished: UI is not running.");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watchdog check failed.");
                DebugFlightRecorder.Log($"Watchdog ERROR: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task InitializeTelegramAsync(CancellationToken stoppingToken)
    {
        try
        {
            DebugFlightRecorder.Log("Telegram init started...");
            if (string.IsNullOrWhiteSpace(_telegramConfig.BotToken))
            {
                DebugFlightRecorder.Log("Telegram init skipped: token is empty.");
                return;
            }

            // Do not perform blocking network calls here; hosted service handles real polling.
            await Task.Delay(1, stoppingToken).ConfigureAwait(false);
            DebugFlightRecorder.Log("Telegram init task finished.");
        }
        catch (Exception ex)
        {
            DebugFlightRecorder.Log($"Telegram init ERROR: {ex.Message}");
        }
    }
}
