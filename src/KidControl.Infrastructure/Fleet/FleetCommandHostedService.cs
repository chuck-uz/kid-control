using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Managed-mode command loop (§6): long-polls <c>GET /agent/commands</c>, applies each command
/// exactly once (dedup by id via <see cref="IProcessedCommandStore"/>, since delivery is
/// at-least-once), and acks the results. The backend only hands out unexpired commands, so a
/// command that outlived its TTL is simply never applied. Runs alongside the heartbeat loop;
/// enrollment is owned by the heartbeat service, so here we just wait for the device identity.
/// </summary>
public sealed class FleetCommandHostedService(
    FleetConfig config,
    FleetClient client,
    IDeviceIdentityStore identityStore,
    IProcessedCommandStore processed,
    FleetCommandApplier applier,
    TimeProvider clock,
    ILogger<FleetCommandHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdentityWait = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.IsManaged)
            return;

        var identity = await WaitForIdentityAsync(stoppingToken);
        if (identity is null)
            return;
        client.UseToken(identity.Token);

        var waitSeconds = (int)Math.Clamp(config.CommandPollTimeout.TotalSeconds, 1, 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var commands = await client.PollCommandsAsync(waitSeconds, stoppingToken);
                if (commands.Count > 0)
                    await ProcessBatchAsync(commands, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Command loop iteration failed; retrying shortly.");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<CommandDto> commands, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var acks = new List<CommandAckDto>(commands.Count);

        foreach (var command in commands)
        {
            if (command.IsExpired(now))
                continue; // TTL lapsed in-flight: ignore (don't apply, don't ack)

            if (processed.Contains(command.Id))
            {
                // Already applied; a redelivery just needs re-acking (idempotent).
                acks.Add(new CommandAckDto(command.Id, true));
                continue;
            }

            var (ok, error) = await applier.ApplyAsync(command, ct);
            if (ok)
                processed.Add(command.Id);
            acks.Add(new CommandAckDto(command.Id, ok, error));
        }

        processed.Save();
        if (acks.Count > 0)
            await client.AckCommandsAsync(new CommandAckBatch(acks), ct);
    }

    private async Task<DeviceIdentity?> WaitForIdentityAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var identity = identityStore.Load();
            if (identity is not null)
                return identity;
            try { await Task.Delay(IdentityWait, ct); }
            catch (OperationCanceledException) { break; }
        }
        return null;
    }
}
