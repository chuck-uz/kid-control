namespace KidControl.Backend.Entities;

/// <summary>
/// Accumulated active-use seconds per device per local (Tashkent) day — the data behind the
/// dashboard's per-day screen-time charts. The heartbeat handler adds each short online gap
/// during which the device was actively in use (Playing phase). One row per (device, day).
/// </summary>
public sealed class DeviceUsageDaily
{
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>The local (UTC+5) calendar day the usage is attributed to.</summary>
    public DateOnly Day { get; set; }

    /// <summary>Accumulated active-use seconds for that day.</summary>
    public long Seconds { get; set; }
}
