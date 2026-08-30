namespace KidControl.Backend.Fleet;

/// <summary>
/// Anti-spam for content-monitor alerts (RFC-05 §7): a 60-second cooldown per
/// (device, category, term, source) plus a per-device ceiling of 10 alerts/minute. When the
/// ceiling is first crossed a single "collapsed" notice is emitted, and the rest of that minute
/// is suppressed. Pure decision logic (time is passed in) so it is unit-tested directly.
/// </summary>
public sealed class WordAlertTracker
{
    public enum Decision
    {
        /// <summary>Send the full alert (with screenshot).</summary>
        Send,

        /// <summary>Drop silently (duplicate within cooldown, or already collapsed this minute).</summary>
        Suppress,

        /// <summary>Send a single "too many — collapsed" notice for this minute.</summary>
        Rollup
    }

    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private const int MaxPerMinute = 10;

    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, RateState> _rate = new();

    private struct RateState
    {
        public DateTimeOffset WindowStart;
        public int Count;
        public bool RolledUp;
    }

    public Decision Decide(Guid deviceId, string category, string term, string source, DateTimeOffset now)
    {
        lock (_sync)
        {
            // Per-key cooldown: identical hit within 60s → drop.
            var key = $"{deviceId}|{category}|{term}|{source}";
            if (_lastByKey.TryGetValue(key, out var last) && now - last < Cooldown)
            {
                return Decision.Suppress;
            }
            _lastByKey[key] = now;
            PruneKeys(now);

            // Per-device rate ceiling with a single rollup notice.
            var r = _rate.TryGetValue(deviceId, out var st) ? st : new RateState { WindowStart = now };
            if (now - r.WindowStart >= RateWindow)
            {
                r = new RateState { WindowStart = now };
            }
            r.Count++;

            Decision d;
            if (r.Count <= MaxPerMinute)
            {
                d = Decision.Send;
            }
            else if (!r.RolledUp)
            {
                r.RolledUp = true;
                d = Decision.Rollup;
            }
            else
            {
                d = Decision.Suppress;
            }

            _rate[deviceId] = r;
            return d;
        }
    }

    private void PruneKeys(DateTimeOffset now)
    {
        if (_lastByKey.Count < 2000)
        {
            return; // keep it cheap; only sweep when the map grows
        }

        foreach (var kv in _lastByKey.Where(kv => now - kv.Value > Cooldown).ToList())
        {
            _lastByKey.Remove(kv.Key);
        }
    }
}
