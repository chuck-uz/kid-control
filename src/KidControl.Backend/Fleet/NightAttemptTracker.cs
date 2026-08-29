using System.Collections.Concurrent;

namespace KidControl.Backend.Fleet;

/// <summary>
/// In-memory anti-spam for night-usage-attempt alerts (H2). The agent reports the UTC time of
/// its most recent throttled night attempt on every heartbeat; we alert operators once per new
/// value. Kept in memory deliberately: a backend restart may re-alert a single recent attempt,
/// which is harmless and avoids a schema change just for dedup state.
/// </summary>
public sealed class NightAttemptTracker
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastAlerted = new();

    /// <summary>
    /// True (and records the new time) if <paramref name="attemptAt"/> is a real, newer attempt
    /// than the last one alerted for this device; false for null or a repeat/older value.
    /// </summary>
    public bool ShouldAlert(Guid deviceId, DateTimeOffset? attemptAt)
    {
        if (attemptAt is not { } at)
            return false;

        var prev = _lastAlerted.TryGetValue(deviceId, out var p) ? p : DateTimeOffset.MinValue;
        if (at <= prev)
            return false;

        _lastAlerted[deviceId] = at;
        return true;
    }
}
