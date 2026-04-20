using System.IO.Pipes;
using System.Text.Json;
using KidControl.Contracts;
using Serilog;
using StreamReader = System.IO.StreamReader;

namespace KidControl.UiHost.Services;

public sealed class NamedPipeClient
{
    private const string PipeName = "KidControlPipe";
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
        _cts.Cancel();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.In,
                    PipeOptions.Asynchronous);

                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(client);

                while (!cancellationToken.IsCancellationRequested && client.IsConnected)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        break;
                    }

                    try
                    {
                        var state = JsonSerializer.Deserialize<SessionStateDto>(line, JsonOptions);
                        if (state is not null)
                        {
                            OnStateReceived?.Invoke(state);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "NamedPipeClient failed to deserialize SessionStateDto payload.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "NamedPipeClient connection/read error.");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
