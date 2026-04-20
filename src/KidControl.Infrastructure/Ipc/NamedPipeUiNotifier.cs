using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KidControl.Application.Interfaces;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

public sealed class NamedPipeUiNotifier(ILogger<NamedPipeUiNotifier> logger) : IUiNotifier
{
    private const string PipeName = "KidControlPipe";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _singleWriter = new(1, 1);

    public async Task NotifyStateChangedAsync(SessionStateDto state)
    {
        await _singleWriter.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(payload);
            await server.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await server.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("UI pipe client is not connected yet.");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed writing state to named pipe.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected named pipe notification error.");
        }
        finally
        {
            _singleWriter.Release();
        }
    }
}
