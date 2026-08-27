using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// The managed-mode agent loop (RFC §6–8). On start it enrolls (or loads its token), applies
/// the last cached policy immediately so enforcement is correct even before the network is up
/// (offline-first, §7), then heartbeats on an interval: status up, policy/desired delta down.
/// A returned policy is applied and cached; a backend outage just leaves the cached policy in
/// force. Desired-state APPLICATION (pause/block) arrives in T7 — here it is only cached.
/// </summary>
public sealed class FleetHeartbeatHostedService(
    FleetConfig config,
    FleetEnrollmentService enrollment,
    FleetClient client,
    IDeviceIdentityStore identityStore,
    IFleetStateStore stateStore,
    FleetPolicyApplier applier,
    SessionService session,
    AgentInfo agent,
    ILogger<FleetHeartbeatHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.IsManaged)
            return;

        var state = stateStore.Load();

        // Offline-first: enforce the last known policy immediately, before touching the network.
        if (state.Policy is not null)
        {
            try { await applier.ApplyAsync(state.Policy, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to apply cached policy on startup.");
            }
        }

        // Enroll (or load token). Non-fatal: without a token we keep enforcing the cache.
        try { await enrollment.EnsureEnrolledAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Enrollment step failed; continuing on cached policy.");
        }

        var identity = identityStore.Load();
        if (identity is null)
        {
            logger.LogWarning("Fleet: no device identity; heartbeat loop idle (cached policy still enforced).");
            return;
        }
        client.UseToken(identity.Token);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatOnceAsync(state, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Heartbeat iteration failed; will retry.");
            }

            try { await Task.Delay(config.HeartbeatInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task HeartbeatOnceAsync(FleetState state, CancellationToken ct)
    {
        var live = session.GetCurrentState();
        var status = new StatusReportDto
        {
            Status = live.Status,
            TimeRemaining = live.TimeRemaining,
            IsNight = live.IsNightMode,
            IsUnlimited = live.IsUnlimited,
            ShutdownInSeconds = live.ShutdownInSeconds,
            AgentVersion = agent.AgentVersion
        };

        var request = new HeartbeatRequest
        {
            Status = status,
            PolicyVersion = state.PolicyVersion,
            DesiredVersion = state.DesiredVersion
        };

        var resp = await client.HeartbeatAsync(request, ct);
        if (resp is null)
            return; // unreachable → stay on cache

        var changed = false;

        if (resp.Policy is not null && resp.Policy.Version > state.PolicyVersion)
        {
            await applier.ApplyAsync(resp.Policy, ct);
            state.Policy = resp.Policy;
            changed = true;
        }

        if (resp.Desired is not null && resp.Desired.Version > state.DesiredVersion)
        {
            // T6 caches the desired-state; APPLYING it (pause/block/bypass) lands in T7.
            state.Desired = resp.Desired;
            changed = true;
            logger.LogInformation("Fleet: desired-state v{Version} received (application in T7).", resp.Desired.Version);
        }

        if (changed)
            stateStore.Save(state);
    }
}
