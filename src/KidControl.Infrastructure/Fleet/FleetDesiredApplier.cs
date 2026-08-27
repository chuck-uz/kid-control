using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Reconciles the local session to the backend's desired-state overrides (§6). T7 handles the
/// <c>paused</c> override (durable, shown centrally); force-block and night-bypass follow in T9.
/// Idempotent — it only issues Pause/Resume when the current state actually differs, so
/// re-applying the same desired snapshot is a no-op.
/// </summary>
public sealed class FleetDesiredApplier(SessionService session, ILogger<FleetDesiredApplier> logger)
{
    public async Task ApplyAsync(DesiredStateDto desired, CancellationToken ct = default)
    {
        if (desired.Paused && !session.IsPaused())
        {
            await session.ExecuteAsync(new SessionCommand.Pause(), ct).ConfigureAwait(false);
            logger.LogInformation("Fleet: paused by desired-state v{Version}.", desired.Version);
        }
        else if (!desired.Paused && session.IsPaused())
        {
            await session.ExecuteAsync(new SessionCommand.Resume(), ct).ConfigureAwait(false);
            logger.LogInformation("Fleet: resumed by desired-state v{Version}.", desired.Version);
        }
    }
}
