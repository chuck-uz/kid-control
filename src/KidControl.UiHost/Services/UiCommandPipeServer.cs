using System.IO.Pipes;
using System.IO;
using Serilog;

namespace KidControl.UiHost.Services;

public sealed class UiCommandPipeServer(MainWindow mainWindow)
{
    private const string PipeName = "KidControlUiCommandPipe";
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

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
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                await using var writer = new StreamWriter(server) { AutoFlush = true };

                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(command, "GET_SCREENSHOT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("ERROR|UNKNOWN_COMMAND").ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var screenshotPath = await mainWindow.CaptureScreenshotAsync().ConfigureAwait(false);
                    await writer.WriteLineAsync($"OK|{screenshotPath}").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "UI screenshot capture failed.");
                    await writer.WriteLineAsync("ERROR|CAPTURE_FAILED").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "UiCommandPipeServer iteration failed.");
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
