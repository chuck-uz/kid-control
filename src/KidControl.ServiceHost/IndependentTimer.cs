using KidControl.Application.Services;
using Microsoft.Extensions.Logging;

namespace KidControl.ServiceHost;

/// <summary>
/// Dedicated timer loop that only advances session time.
/// </summary>
public sealed class IndependentTimer(
    SessionOrchestrator orchestrator,
    ILogger<IndependentTimer> logger)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await orchestrator.ProcessTickAsync().ConfigureAwait(false);
                var state = orchestrator.GetCurrentState();
                DebugFlightRecorder.Log($"TICK: {state.TimeRemaining:hh\\:mm\\:ss}; Status={state.Status}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Independent timer tick failed.");
                DebugFlightRecorder.Log($"TICK ERROR: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

