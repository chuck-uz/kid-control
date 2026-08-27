using KidControl.Fleet.Contracts;

namespace KidControl.Backend.Entities;

/// <summary>
/// Long-lived, versioned overrides (§6 "synchronised state") that must survive being set
/// while a device is offline and be reconciled on reconnect: pause, force-block, and a
/// timed night-bypass. Separate from <see cref="DevicePolicy"/> so an override doesn't bump
/// (or get clobbered by) a policy edit.
/// </summary>
public sealed class DeviceDesired
{
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>Control fully suspended (agent hides UI, no timing).</summary>
    public bool Paused { get; set; }

    /// <summary>Manual block ignoring timer transitions until cleared.</summary>
    public bool ForceBlocked { get; set; }

    /// <summary>If set and in the future, the night block is bypassed until this moment.</summary>
    public DateTimeOffset? NightBypassUntil { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DesiredStateDto ToDto() => new()
    {
        Version = Version,
        Paused = Paused,
        ForceBlocked = ForceBlocked,
        NightBypassUntil = NightBypassUntil
    };
}
