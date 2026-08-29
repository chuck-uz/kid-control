using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Relays an operator-requested screenshot back to the chat that asked for it (G1). The bot
/// enqueues a <c>screenshot</c> command carrying an <c>uploadId</c> and registers
/// (uploadId → chat, device) here; the agent captures the screen and POSTs the image to
/// <c>/agent/media?uploadId=…</c>; that endpoint hands the bytes to <see cref="DeliverAsync"/>,
/// which sends the photo to the requesting chat.
///
/// State is in-memory with a short TTL: a screenshot is a live request, so a backend restart
/// mid-flight just drops it (the operator retries). The uploading device MUST match the one the
/// screenshot was requested from, so a device can never push into another device's request.
/// </summary>
public class ScreenshotRelay(ITelegramBotClient bot)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(3);

    /// <summary>Reject anything larger than this (a screenshot JPEG/PNG is well under it).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Pending(long ChatId, Guid DeviceId, DateTimeOffset At);

    public void Register(string uploadId, long chatId, Guid deviceId)
    {
        Prune();
        _pending[uploadId] = new Pending(chatId, deviceId, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Deliver an uploaded image to the chat that requested it. Returns false (and sends nothing)
    /// if there is no matching pending request, the device doesn't match, the request has expired,
    /// or the payload is empty/too large.
    /// </summary>
    public async Task<bool> DeliverAsync(string uploadId, Guid deviceId, byte[] image, CancellationToken ct = default)
    {
        if (image.Length == 0 || image.Length > MaxBytes)
            return false;
        if (!_pending.TryGetValue(uploadId, out var p) || p.DeviceId != deviceId)
            return false;

        _pending.TryRemove(uploadId, out _);
        if (DateTimeOffset.UtcNow - p.At > Ttl)
            return false;

        await SendPhotoAsync(p.ChatId, image, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sends the photo to the chat. Overridable so tests can assert delivery without Telegram.</summary>
    protected virtual Task SendPhotoAsync(long chatId, byte[] image, CancellationToken ct)
        => bot.SendPhoto(chatId, InputFile.FromStream(new MemoryStream(image), "screenshot.jpg"),
            caption: "📷 Скриншот", cancellationToken: ct);

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kv in _pending)
        {
            if (kv.Value.At < cutoff)
                _pending.TryRemove(kv.Key, out _);
        }
    }
}
