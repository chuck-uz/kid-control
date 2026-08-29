using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Applies a backend <see cref="PolicyDto"/> to the local session by replaying it as the
/// same <see cref="SessionCommand"/>s an operator would issue — so managed and standalone
/// share one, already-tested enforcement path (RFC §8). The rule is applied last because it
/// resets the current phase's timer; doing so only happens when a new policy version actually
/// arrives (the heartbeat returns a policy only when the agent is behind), so a steady state
/// never resets the timer.
/// </summary>
public sealed class FleetPolicyApplier(
    SessionService session, FleetUpdateTarget updateTarget, ILogger<FleetPolicyApplier> logger)
{
    /// <summary>Pure translation of a policy into the ordered commands that realise it.</summary>
    public static IReadOnlyList<SessionCommand> ToCommands(PolicyDto policy) =>
    [
        new SessionCommand.SetNight(policy.ToNightWindow()),
        new SessionCommand.SetNightEnabled(policy.NightEnabled),
        new SessionCommand.SetRule(policy.ToScheduleRule()),
        // Intervals LAST: it has the final word on the countdown. If intervals are OFF it must
        // clear TimeRemaining — applying the rule afterwards would wrongly repopulate it, so the
        // "unlimited" device would still show a leftover time.
        new SessionCommand.SetIntervals(policy.IntervalsEnabled)
    ];

    public async Task ApplyAsync(PolicyDto policy, CancellationToken ct = default)
    {
        foreach (var command in ToCommands(policy))
            await session.ExecuteAsync(command, ct).ConfigureAwait(false);

        // Hybrid self-update (§9): the backend dictates which version to run.
        updateTarget.Set(policy.TargetVersion);

        logger.LogInformation(
            "Applied fleet policy v{Version}: {Play}/{Rest} min, night {NightEnabled} {NightStart}-{NightEnd}, intervals {Intervals}, target {Target}.",
            policy.Version, policy.PlayMinutes, policy.RestMinutes, policy.NightEnabled,
            policy.NightStart, policy.NightEnd, policy.IntervalsEnabled, policy.TargetVersion);
    }
}
