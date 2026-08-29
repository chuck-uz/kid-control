using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// One reconciliation cycle (RFC §6): heartbeat first — apply the newest policy, then bring
/// local overrides to <c>desired</c> — and only THEN drain the command queue in order,
/// dropping TTL-expired commands and applying each at-most-once. This ordering matters on
/// reconnect: policy/state must settle before one-shot commands run on top of them.
///
/// Everything is offline-tolerant: a null heartbeat / empty poll (backend down) simply leaves
/// the cached policy in force. The agent keeps enforcing <see cref="ApplyCachedAsync"/>'s
/// result regardless of connectivity. Extracted from the hosted service so the whole
/// offline→reconnect flow is unit-testable against a fake <see cref="IFleetClient"/>.
/// </summary>
public sealed class FleetReconciler(
    IFleetClient client,
    IFleetStateStore stateStore,
    IProcessedCommandStore processed,
    FleetPolicyApplier policyApplier,
    FleetDesiredApplier desiredApplier,
    FleetCommandApplier commandApplier,
    SessionService session,
    AgentInfo agent,
    TimeProvider clock,
    ILogger<FleetReconciler> logger)
{
    private readonly FleetState _state = stateStore.Load();

    /// <summary>Attach the device token to the backend client (the reconciler's own instance).</summary>
    public void UseToken(string token) => client.UseToken(token);

    /// <summary>Enforce the last cached policy + desired immediately, before any network I/O.</summary>
    public async Task ApplyCachedAsync(CancellationToken ct = default)
    {
        if (_state.Policy is not null)
            await SafeApply(() => policyApplier.ApplyAsync(_state.Policy, ct), "cached policy");
        if (_state.Desired is not null)
            await SafeApply(() => desiredApplier.ApplyAsync(_state.Desired, ct), "cached desired-state");
    }

    /// <summary>One full cycle: heartbeat (policy → desired) then drain commands. Order guaranteed.</summary>
    public async Task ReconcileOnceAsync(int commandWaitSeconds, CancellationToken ct = default)
    {
        await HeartbeatAsync(ct);
        await DrainCommandsAsync(commandWaitSeconds, ct);
    }

    /// <summary>Heartbeat: report status, apply any newer policy then desired, persist the cache.</summary>
    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        var live = session.GetCurrentState();
        var request = new HeartbeatRequest
        {
            Status = new StatusReportDto
            {
                Status = live.Status,
                TimeRemaining = live.TimeRemaining,
                IsNight = live.IsNightMode,
                IsUnlimited = live.IsUnlimited,
                ShutdownInSeconds = live.ShutdownInSeconds,
                AgentVersion = agent.AgentVersion,
                LastNightAttemptAt = live.LastNightAttemptAt
            },
            PolicyVersion = _state.PolicyVersion,
            DesiredVersion = _state.DesiredVersion
        };

        var resp = await client.HeartbeatAsync(request, ct);
        if (resp is null)
            return; // unreachable → stay on cache

        var changed = false;

        // Policy BEFORE desired (§6): the rule/window settle first, then overrides on top.
        if (resp.Policy is not null && resp.Policy.Version > _state.PolicyVersion)
        {
            await policyApplier.ApplyAsync(resp.Policy, ct);
            _state.Policy = resp.Policy;
            changed = true;
        }

        if (resp.Desired is not null && resp.Desired.Version > _state.DesiredVersion)
        {
            await desiredApplier.ApplyAsync(resp.Desired, ct);
            _state.Desired = resp.Desired;
            changed = true;
        }

        if (changed)
            stateStore.Save(_state);
    }

    /// <summary>Drain the command queue: skip expired, apply once (dedup by id), ack results.</summary>
    public async Task DrainCommandsAsync(int commandWaitSeconds, CancellationToken ct = default)
    {
        var commands = await client.PollCommandsAsync(commandWaitSeconds, ct);
        if (commands.Count == 0)
            return;

        var now = clock.GetUtcNow();
        var acks = new List<CommandAckDto>(commands.Count);

        foreach (var command in commands)
        {
            if (command.IsExpired(now))
                continue; // TTL lapsed in-flight → ignore (don't apply, don't ack)

            if (processed.Contains(command.Id))
            {
                acks.Add(new CommandAckDto(command.Id, true)); // already applied → re-ack only
                continue;
            }

            // Pass this reconciler's authenticated client so media uploads (screenshot) go out
            // as this device (a freshly-injected client would be unauthenticated).
            var (ok, error) = await commandApplier.ApplyAsync(command, client, ct);
            if (ok)
                processed.Add(command.Id);
            acks.Add(new CommandAckDto(command.Id, ok, error));
        }

        processed.Save();
        if (acks.Count > 0)
            await client.AckCommandsAsync(new CommandAckBatch(acks), ct);
    }

    private async Task SafeApply(Func<Task> apply, string what)
    {
        try { await apply(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to apply {What}.", what);
        }
    }
}
