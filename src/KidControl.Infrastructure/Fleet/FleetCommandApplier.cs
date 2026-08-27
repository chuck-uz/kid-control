using KidControl.Application.Abstractions;
using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Executes one-shot fleet commands against the local agent. T10 covers the full non-media
/// set: <c>add_time</c>, <c>reset_timer</c>, <c>shutdown</c>, <c>restart</c> (through the shared
/// <see cref="SessionService"/> path) and <c>update_now</c> (through the update service, honoring
/// a pinned target or an explicit tag). The media verbs <c>screenshot</c>/<c>play_audio</c> are
/// Phase 2: they're acked as not-yet-supported rather than left to redeliver.
/// </summary>
public sealed class FleetCommandApplier(
    SessionService session,
    IUpdateService update,
    FleetUpdateTarget updateTarget,
    ILogger<FleetCommandApplier> logger)
{
    /// <summary>Pure mapping of a command to a session command, or null if it isn't one.</summary>
    public static SessionCommand? ToSessionCommand(CommandDto command) => command.Type switch
    {
        CommandTypes.AddTime =>
            command.GetInt("minutes") is int m && m > 0 ? new SessionCommand.AddTime(m) : null,
        CommandTypes.ResetTimer => new SessionCommand.ResetTimer(),
        CommandTypes.Shutdown => new SessionCommand.ShutdownPc(),
        CommandTypes.Restart => new SessionCommand.RestartPc(),
        _ => null
    };

    public async Task<(bool Ok, string? Error)> ApplyAsync(CommandDto command, CancellationToken ct = default)
    {
        try
        {
            switch (command.Type)
            {
                case CommandTypes.UpdateNow:
                    return await ApplyUpdateNowAsync(command, ct);

                // Phase 2 (media relay): acked so they don't redeliver, but not executed yet.
                case CommandTypes.Screenshot:
                case CommandTypes.PlayAudio:
                    logger.LogInformation("Fleet command {Type} ({Id}) is Phase 2 — acked, not executed.",
                        command.Type, command.Id);
                    return (false, $"{command.Type} is Phase 2 (media relay)");

                default:
                    var sessionCommand = ToSessionCommand(command);
                    if (sessionCommand is null)
                        return (false, $"unsupported command: {command.Type}");
                    await session.ExecuteAsync(sessionCommand, ct).ConfigureAwait(false);
                    logger.LogInformation("Applied fleet command {Type} ({Id}).", command.Type, command.Id);
                    return (true, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Command {Type} ({Id}) failed.", command.Type, command.Id);
            return (false, ex.Message);
        }
    }

    private async Task<(bool Ok, string? Error)> ApplyUpdateNowAsync(CommandDto command, CancellationToken ct)
    {
        // Explicit tag wins; otherwise honor a pinned target; otherwise resolve the latest release.
        var tag = command.GetString("tag");
        if (string.IsNullOrWhiteSpace(tag))
            tag = updateTarget.IsPinned ? updateTarget.Current : null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            var info = await update.CheckAsync(ct).ConfigureAwait(false);
            if (info is null)
                return (true, "already up to date");
            tag = info.Tag;
        }

        var normalized = tag.StartsWith('v') || tag.StartsWith('V') ? tag : "v" + tag;
        logger.LogInformation("Fleet update_now ({Id}): installing {Tag}.", command.Id, normalized);
        await update.StartInstallAsync(normalized, ct).ConfigureAwait(false);
        return (true, $"installing {normalized}");
    }
}
