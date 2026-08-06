using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using KidControl.Contracts;
using Serilog;

namespace KidControl.UiHost.Services;

/// <summary>
/// Listens on the service→UI state pipe (<see cref="KidControlNames.StatePipe"/>), reading
/// newline-delimited JSON <see cref="SessionStateDto"/> payloads and raising
/// <see cref="OnStateReceived"/> for each. Reconnects automatically if the service restarts.
/// </summary>
public sealed class StatePipeClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public event Action<SessionStateDto>? OnStateReceived;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    KidControlNames.StatePipe,
                    PipeDirection.In,
                    PipeOptions.Asynchronous);

                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(client);

                while (!cancellationToken.IsCancellationRequested && client.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var state = JsonSerializer.Deserialize<SessionStateDto>(line, JsonOptions);
                        if (state is not null)
                        {
                            OnStateReceived?.Invoke(state);
                        }
                    }
                    catch (JsonException ex)
                    {
                        Log.Error(ex, "StatePipeClient failed to deserialize SessionStateDto payload.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StatePipeClient connection/read error.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
