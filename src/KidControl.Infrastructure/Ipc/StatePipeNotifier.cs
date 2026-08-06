using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using KidControl.Application.Abstractions;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

/// <summary>
/// <see cref="IUiNotifier"/> that pushes one JSON line of <see cref="SessionStateDto"/>
/// per notification over the state pipe. A single-writer semaphore serialises writes so
/// concurrent ticks never interleave on the pipe, and a short connect timeout means the
/// timer loop is never blocked waiting for a UI client that is not listening.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatePipeNotifier : IUiNotifier, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _singleWriter = new(1, 1);
    private readonly ILogger<StatePipeNotifier> _logger;

    public StatePipeNotifier(ILogger<StatePipeNotifier> logger) => _logger = logger;

    public async Task NotifyStateChangedAsync(SessionStateDto state, CancellationToken ct = default)
    {
        await _singleWriter.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var security = PipeAccess.CreateStatePipe();
            await using var server = NamedPipeServerStreamAcl.Create(
                KidControlNames.StatePipe,
                PipeDirection.Out,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 64 * 1024,
                security);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);

            await server.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(state, JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await server.WriteAsync(bytes, ct).ConfigureAwait(false);
            await server.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // No UI client connected within the timeout — expected, harmless.
            _logger.LogDebug("State pipe: no UI client connected.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push state over the pipe.");
        }
        finally
        {
            _singleWriter.Release();
        }
    }

    public void Dispose() => _singleWriter.Dispose();
}
