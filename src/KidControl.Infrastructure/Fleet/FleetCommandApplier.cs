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
    Ipc.IUiCommandClient uiCommands,
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

    public async Task<(bool Ok, string? Error)> ApplyAsync(CommandDto command, IFleetClient fleet,
        CancellationToken ct = default)
    {
        try
        {
            switch (command.Type)
            {
                case CommandTypes.UpdateNow:
                    return await ApplyUpdateNowAsync(command, ct);

                case CommandTypes.Screenshot:
                    return await ApplyScreenshotAsync(command, fleet, ct);

                case CommandTypes.PlayAudio:
                    return await ApplyPlayAudioAsync(command, fleet, ct);

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

    /// <summary>
    /// Capture the screen (via the interactive-session UI) and upload it to the backend, which
    /// relays it to the operator who asked. The uploadId ties the image to that request.
    /// </summary>
    private async Task<(bool Ok, string? Error)> ApplyScreenshotAsync(CommandDto command, IFleetClient fleet,
        CancellationToken ct)
    {
        var uploadId = command.GetString("uploadId");
        if (string.IsNullOrWhiteSpace(uploadId))
            return (false, "screenshot: missing uploadId");

        var path = await uiCommands.CaptureScreenshotAsync(ct).ConfigureAwait(false);
        if (path is null)
            return (false, "screenshot: capture failed (UI not running?)");

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var ok = await fleet.UploadMediaAsync(uploadId, bytes, ct).ConfigureAwait(false);
            logger.LogInformation("Fleet screenshot ({Id}): {Bytes} bytes, upload {Result}.",
                command.Id, bytes.Length, ok ? "ok" : "rejected");
            return ok ? (true, null) : (false, "screenshot: upload rejected");
        }
        finally
        {
            try { File.Delete(path); } catch (Exception ex) { logger.LogDebug(ex, "Temp screenshot cleanup failed."); }
        }
    }

    /// <summary>
    /// Download an operator-sent audio clip from the backend and play it in the interactive
    /// session (via the UI). The mediaId ties it to the clip the operator queued.
    /// </summary>
    private async Task<(bool Ok, string? Error)> ApplyPlayAudioAsync(CommandDto command, IFleetClient fleet,
        CancellationToken ct)
    {
        var mediaId = command.GetString("mediaId");
        if (string.IsNullOrWhiteSpace(mediaId))
            return (false, "play_audio: missing mediaId");

        var bytes = await fleet.DownloadMediaAsync(mediaId, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
            return (false, "play_audio: download failed");

        // Telegram voice notes are OGG/Opus; the UI's player handles them.
        var path = Path.Combine(Path.GetTempPath(), $"kc-audio-{Guid.NewGuid():N}.ogg");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            var ok = await uiCommands.PlayAudioAsync(path, ct).ConfigureAwait(false);
            logger.LogInformation("Fleet play_audio ({Id}): {Bytes} bytes, play {Result}.",
                command.Id, bytes.Length, ok ? "ok" : "failed");
            return ok ? (true, null) : (false, "play_audio: UI playback failed (UI not running?)");
        }
        finally
        {
            try { File.Delete(path); } catch (Exception ex) { logger.LogDebug(ex, "Temp audio cleanup failed."); }
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
