using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// The managed-mode agent loop (RFC §6–8). Startup enforces the cached policy/desired FIRST
/// (offline-first — correct even with no network), then enrolls, then loops one
/// <see cref="FleetReconciler"/> cycle at a time (heartbeat → commands). A backend outage is a
/// no-op: the cached policy stays in force and the loop keeps retrying. The command long-poll
/// inside each cycle also paces the loop, so an idle agent isn't busy-spinning.
/// </summary>
public sealed class FleetAgentHostedService(
    FleetConfig config,
    FleetEnrollmentService enrollment,
    FleetReconciler reconciler,
    IDeviceIdentityStore identityStore,
    ILogger<FleetAgentHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.IsManaged)
            return;

        // 1. Offline-first: enforce the last known policy + desired before any network I/O.
        await reconciler.ApplyCachedAsync(stoppingToken);

        // 2. Enroll (or load token). Non-fatal: without a token we keep enforcing the cache.
        try { await enrollment.EnsureEnrolledAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Enrollment failed; continuing on cached policy.");
        }

        var identity = identityStore.Load();
        if (identity is null)
        {
            logger.LogWarning("Fleet: no device identity; loop idle (cached policy still enforced).");
            return;
        }
        reconciler.UseToken(identity.Token);

        var waitSeconds = (int)Math.Clamp(config.CommandPollTimeout.TotalSeconds, 1, 60);

        // 3. Reconcile: heartbeat (policy → desired) then drain commands, forever.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reconciler.ReconcileOnceAsync(waitSeconds, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconcile cycle failed; retrying shortly.");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
