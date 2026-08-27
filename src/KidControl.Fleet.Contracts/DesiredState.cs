namespace KidControl.Fleet.Contracts;

/// <summary>
/// Long-lived, versioned overrides that must survive being set while a device is offline
/// and be reconciled on reconnect. These are STATE (not one-shot commands): if the parent
/// pauses a device, it stays paused across restarts and is shown centrally as paused.
/// </summary>
public sealed record DesiredStateDto
{
    public int Version { get; init; }

    /// <summary>Control fully suspended (agent hides the UI, no timing).</summary>
    public bool Paused { get; init; }

    /// <summary>Manual block that ignores timer transitions until cleared.</summary>
    public bool ForceBlocked { get; init; }

    /// <summary>If set and in the future, the night block is bypassed until this moment.</summary>
    public DateTimeOffset? NightBypassUntil { get; init; }
}
