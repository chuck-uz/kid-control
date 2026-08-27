namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Fleet (managed-mode) configuration. The single switch is <see cref="Url"/>: unset →
/// standalone (embedded bot, local JSON, exactly as before); set → managed (the agent
/// enrolls, pulls policy/commands, and the embedded bot is turned off). See RFC §8.
/// </summary>
public sealed class FleetConfig
{
    public const string SectionName = "Fleet";

    /// <summary>Backend base URL, e.g. "https://fleet.example.com". Empty ⇒ standalone.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>One-time enrollment code (used once on first boot, then ignored).</summary>
    public string EnrollCode { get; init; } = string.Empty;

    /// <summary>How often to heartbeat the backend (used from T6).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Long-poll wait for the commands endpoint (used from T7).</summary>
    public TimeSpan CommandPollTimeout { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>Managed mode is on when a backend URL is configured.</summary>
    public bool IsManaged => !string.IsNullOrWhiteSpace(Url);

    public bool HasEnrollCode => !string.IsNullOrWhiteSpace(EnrollCode);
}
