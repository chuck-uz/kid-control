using System.IO.Pipes;
using System.Runtime.Versioning;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

/// <summary>
/// Service-side client for the UI command pipe. Asks the interactive-session UI process to
/// capture a screenshot or play an audio file — things the SYSTEM service cannot do itself.
/// Best-effort: any failure (UI not running, timeout) returns null/false rather than throwing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiCommandClient(ILogger<UiCommandClient> logger)
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Asks the UI to capture the screen; returns the PNG path on success, else null.</summary>
    public async Task<string?> CaptureScreenshotAsync(CancellationToken ct = default)
    {
        var path = TransferPaths.NewFile("png");
        var request = $"{UiCommandProtocol.Screenshot}{UiCommandProtocol.Separator}{path}";
        var response = await SendAsync(request, ct).ConfigureAwait(false);

        if (string.Equals(response, UiCommandProtocol.Ok, StringComparison.Ordinal) && File.Exists(path))
        {
            return path;
        }

        logger.LogWarning("Screenshot request failed: response='{Response}'.", response ?? "<none>");
        return null;
    }

    /// <summary>Asks the UI to play an audio file. Returns true on an OK response.</summary>
    public async Task<bool> PlayAudioAsync(string audioPath, CancellationToken ct = default)
    {
        var request = $"{UiCommandProtocol.Play}{UiCommandProtocol.Separator}{audioPath}";
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        var ok = string.Equals(response, UiCommandProtocol.Ok, StringComparison.Ordinal);
        if (!ok)
        {
            logger.LogWarning("Play request failed: response='{Response}'.", response ?? "<none>");
        }

        return ok;
    }

    private async Task<string?> SendAsync(string request, CancellationToken ct)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", KidControlNames.UiCommandPipe, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(client);
            await using var writer = new StreamWriter(client) { AutoFlush = true };

            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(RequestTimeout);
            await writer.WriteLineAsync(request.AsMemory(), reqCts.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(reqCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UI command '{Verb}' failed (UI not running?).", request.Split(UiCommandProtocol.Separator)[0]);
            return null;
        }
    }
}
