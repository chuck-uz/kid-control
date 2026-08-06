using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using KidControl.Application.Abstractions;
using KidControl.Application.Services;
using KidControl.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

/// <summary>
/// Admin-only command channel. Privileged local tools (the unlock utility) connect over
/// the ACL-restricted command pipe to run the emergency-shutdown handshake.
///
/// Robustness notes:
///  * one server instance is accepted at a time, then re-created — a fresh ACL per instance;
///  * each connection is processed under a read timeout linked to the stopping token, so a
///    client that connects and then stalls cannot wedge the server forever.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CommandPipeServer(
    EmergencyOtpService otp,
    ITelegramGateway telegram,
    ISystemController system,
    ILogger<CommandPipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Command pipe server started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var security = PipeAccess.CreateAdminOnly();
                await using var server = NamedPipeServerStreamAcl.Create(
                    KidControlNames.CommandPipe,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4 * 1024,
                    outBufferSize: 4 * 1024,
                    security);

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleConnectionAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command pipe iteration failed.");
                await DelaySafeAsync(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("Command pipe server stopped.");
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(ReadTimeout);

        try
        {
            using var reader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            await using var writer = new StreamWriter(server, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            var (response, shutdown) = await ProcessAsync(line?.Trim(), stoppingToken).ConfigureAwait(false);

            await writer.WriteLineAsync(response.AsMemory(), stoppingToken).ConfigureAwait(false);

            if (shutdown)
            {
                system.StopUi();
                system.RequestServiceStop();
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Command pipe client stalled; dropping connection.");
        }
    }

    private async Task<(string Response, bool Shutdown)> ProcessAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (CommandPipeProtocol.BadRequest, false);
        }

        if (string.Equals(command, CommandPipeProtocol.InitiateEmergencyAuth, StringComparison.Ordinal))
        {
            var code = otp.TryIssue();
            if (code is null)
            {
                return (CommandPipeProtocol.RateLimited, false);
            }

            await BroadcastCodeSafeAsync(code, ct).ConfigureAwait(false);
            return (CommandPipeProtocol.Ok, false);
        }

        if (command.StartsWith(CommandPipeProtocol.EmergencyShutdownPrefix, StringComparison.Ordinal))
        {
            var candidate = command[CommandPipeProtocol.EmergencyShutdownPrefix.Length..];
            return otp.Validate(candidate) switch
            {
                EmergencyOtpService.ValidationResult.Valid => (CommandPipeProtocol.Success, true),
                EmergencyOtpService.ValidationResult.LockedOut => (CommandPipeProtocol.RateLimited, false),
                _ => (CommandPipeProtocol.Denied, false)
            };
        }

        return (CommandPipeProtocol.BadRequest, false);
    }

    private async Task BroadcastCodeSafeAsync(string code, CancellationToken ct)
    {
        try
        {
            await telegram.BroadcastAsync($"🔐 Код экстренного доступа: {code}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast emergency code.");
        }
    }

    private static async Task DelaySafeAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
