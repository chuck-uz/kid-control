using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

public sealed class UiScreenshotRequester(ILogger<UiScreenshotRequester> logger)
{
    private const string PipeName = "KidControlUiCommandPipe";

    public async Task<string?> RequestScreenshotPathAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client);
            await using var writer = new StreamWriter(client) { AutoFlush = true };

            await writer.WriteLineAsync("GET_SCREENSHOT").ConfigureAwait(false);
            var response = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response))
            {
                return null;
            }

            if (!response.StartsWith("OK|", StringComparison.Ordinal))
            {
                logger.LogWarning("UI screenshot request returned non-ok response: {Response}", response);
                return null;
            }

            var path = response["OK|".Length..].Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("UI screenshot request timed out.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UI screenshot request failed.");
            return null;
        }
    }
}
