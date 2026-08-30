using System.IO.Pipes;
using System.Runtime.Versioning;
using KidControl.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

/// <summary>
/// Asks the interactive-session UI to capture a screenshot or play audio. An interface so the
/// fleet command applier can be unit-tested without a real pipe (and without the Windows-only
/// concrete client), and so callers aren't coupled to the platform-specific implementation.
/// </summary>
public interface IUiCommandClient
{
    /// <summary>Capture the screen; returns the PNG file path on success, else null.</summary>
    Task<string?> CaptureScreenshotAsync(CancellationToken ct = default);

    /// <summary>Play an audio file in the interactive session. Returns true on success.</summary>
    Task<bool> PlayAudioAsync(string audioPath, CancellationToken ct = default);

    /// <summary>Enable/disable the content-monitor sensor in the UI (RFC-05). Best-effort.</summary>
    Task SetMonitorAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Service-side client for the UI command pipe. Asks the interactive-session UI process to
/// capture a screenshot or play an audio file — things the SYSTEM service cannot do itself.
/// Best-effort: any failure (UI not running, timeout) returns null/false rather than throwing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiCommandClient(ILogger<UiCommandClient> logger) : IUiCommandClient
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

    /// <summary>Tell the UI to start/stop the content-monitor sensor (RFC-05). Best-effort.</summary>
    public async Task SetMonitorAsync(bool enabled, CancellationToken ct = default)
    {
        var request = $"{UiCommandProtocol.Monitor}{UiCommandProtocol.Separator}{(enabled ? "on" : "off")}";
        await SendAsync(request, ct).ConfigureAwait(false);
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
