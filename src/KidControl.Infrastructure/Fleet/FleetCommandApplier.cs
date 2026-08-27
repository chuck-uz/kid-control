using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Executes one-shot fleet commands against the local session. T7 covers the skeleton verbs
/// <c>add_time</c> and <c>reset_timer</c>; the rest (shutdown/restart/update_now, and the
/// Phase-2 media verbs) arrive in T10. Unsupported verbs are reported as a failed ack rather
/// than left to redeliver forever.
/// </summary>
public sealed class FleetCommandApplier(SessionService session, ILogger<FleetCommandApplier> logger)
{
    /// <summary>Pure mapping of a command to a session command, or null if unsupported here.</summary>
    public static SessionCommand? ToSessionCommand(CommandDto command) => command.Type switch
    {
        CommandTypes.AddTime =>
            command.GetInt("minutes") is int m && m > 0 ? new SessionCommand.AddTime(m) : null,
        CommandTypes.ResetTimer => new SessionCommand.ResetTimer(),
        _ => null
    };

    public async Task<(bool Ok, string? Error)> ApplyAsync(CommandDto command, CancellationToken ct = default)
    {
        var sessionCommand = ToSessionCommand(command);
        if (sessionCommand is null)
            return (false, $"unsupported command in T7: {command.Type}");

        try
        {
            await session.ExecuteAsync(sessionCommand, ct).ConfigureAwait(false);
            logger.LogInformation("Applied fleet command {Type} ({Id}).", command.Type, command.Id);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Command {Type} ({Id}) failed.", command.Type, command.Id);
            return (false, ex.Message);
        }
    }
}
