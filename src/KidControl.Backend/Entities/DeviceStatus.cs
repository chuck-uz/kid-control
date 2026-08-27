namespace KidControl.Backend.Entities;

/// <summary>
/// The agent's last reported live state — display-only (§6: the agent owns the clock). One
/// row per device, overwritten on each heartbeat; never drives enforcement.
/// </summary>
public sealed class DeviceStatus
{
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>Domain <c>SessionStatus</c> name (Playing/Resting/…), stored as text.</summary>
    public string Status { get; set; } = "Playing";

    public TimeSpan TimeRemaining { get; set; }
    public bool IsNight { get; set; }
    public bool IsUnlimited { get; set; }

    /// <summary>Seconds until the night auto-shutdown fires; -1 when not counting down.</summary>
    public int ShutdownInSeconds { get; set; } = -1;

    public DateTimeOffset ReportedAt { get; set; }
}
