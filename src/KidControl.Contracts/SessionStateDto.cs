namespace KidControl.Contracts;

/// <summary>State snapshot pushed from the service to the UI over the state pipe.</summary>
public sealed record SessionStateDto(
    string Status,
    TimeSpan TimeRemaining,
    bool IsNightMode,
    bool IsUnlimited = false,
    int ShutdownInSeconds = -1,
    // UTC time of the most recent night-time usage attempt that passed the throttle, or null.
    // Reported up to the fleet backend (H2) so the operator can be alerted in managed mode.
    DateTimeOffset? LastNightAttemptAt = null);
