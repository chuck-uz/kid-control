namespace KidControl.Domain.Enums;

/// <summary>
/// The lifecycle state of a controlled computer session.
/// </summary>
public enum SessionStatus
{
    /// <summary>Play time is counting down; the computer is usable.</summary>
    Playing,

    /// <summary>Scheduled rest break; the rest timer is counting down.</summary>
    Resting,

    /// <summary>Manually blocked by a parent; ignores timer transitions until released.</summary>
    ForceBlocked,

    /// <summary>Control is suspended entirely (parent-initiated); timers frozen, UI hidden.</summary>
    Paused,

    /// <summary>Night window is active and blocking; usage is denied until morning.</summary>
    NightBlocked
}
