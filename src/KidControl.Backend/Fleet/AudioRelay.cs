using System.Collections.Concurrent;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Holds an operator-sent audio clip until the target agent fetches it (G2). The bot downloads
/// the Telegram voice/audio, stores the bytes here keyed by a random <c>mediaId</c> tied to the
/// target device, and queues a <c>play_audio</c> command carrying that id; the agent pulls the
/// bytes from <c>/agent/audio?mediaId=…</c> and plays them. One-shot + short TTL: the clip is
/// removed on fetch, and only the device it was addressed to can take it.
/// </summary>
public sealed class AudioRelay
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Reject anything larger than this (a voice note is far smaller).</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Pending(Guid DeviceId, byte[] Audio, DateTimeOffset At);

    /// <summary>Stash a clip for <paramref name="deviceId"/>; returns the mediaId to put in the command.</summary>
    public string Store(Guid deviceId, byte[] audio)
    {
        Prune();
        var mediaId = Guid.NewGuid().ToString("N");
        _pending[mediaId] = new Pending(deviceId, audio, DateTimeOffset.UtcNow);
        return mediaId;
    }

    /// <summary>
    /// Fetch and remove the clip for <paramref name="mediaId"/>, but only for the device it was
    /// addressed to and only within TTL; otherwise null.
    /// </summary>
    public byte[]? Take(string mediaId, Guid deviceId)
    {
        if (!_pending.TryGetValue(mediaId, out var p) || p.DeviceId != deviceId)
            return null;

        _pending.TryRemove(mediaId, out _);
        return DateTimeOffset.UtcNow - p.At > Ttl ? null : p.Audio;
    }

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
