using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Reconciles the local session to the backend's full desired-state (§6): <c>paused</c>,
/// <c>force_blocked</c>, and <c>night_bypass_until</c>. Precedence: <c>paused</c> is strongest
/// (control suspended) — while paused, force-block isn't reconciled so it can't clobber the
/// pause. The night-bypass window is always set from desired (authoritative). Idempotent:
/// each sub-state is only changed when it actually differs, so re-applying is a no-op.
/// </summary>
public sealed class FleetDesiredApplier(SessionService session, ILogger<FleetDesiredApplier> logger)
{
    public async Task ApplyAsync(DesiredStateDto desired, CancellationToken ct = default)
    {
        // Night-bypass is orthogonal to pause/block — always assert the desired window.
        await session.ExecuteAsync(new SessionCommand.SetNightBypass(desired.NightBypassUntil), ct)
            .ConfigureAwait(false);

        if (desired.Paused)
        {
            if (!session.IsPaused())
            {
                await session.ExecuteAsync(new SessionCommand.Pause(), ct).ConfigureAwait(false);
                logger.LogInformation("Fleet: paused by desired-state v{Version}.", desired.Version);
            }
            return; // paused wins — don't reconcile force-block underneath it
        }

        if (session.IsPaused())
        {
            await session.ExecuteAsync(new SessionCommand.Resume(), ct).ConfigureAwait(false);
            logger.LogInformation("Fleet: resumed by desired-state v{Version}.", desired.Version);
        }

        // Reconcile force-block (idempotent inside SessionService).
        await session.ExecuteAsync(new SessionCommand.SetForceBlocked(desired.ForceBlocked), ct)
            .ConfigureAwait(false);
    }
}
