using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;

namespace KidControl.Domain.Entities;

/// <summary>
/// Aggregate root modelling one controlled computer session: the play/rest
/// countdown, forced blocks, pause, and the night-block window.
///
/// Design note: this entity is deliberately clock-free. Time only enters through
/// <see cref="Tick"/>, so every transition is deterministic and unit-testable.
/// The night-block "snapshot/restore" bookkeeping that used to leak into the
/// application orchestrator now lives here, where it belongs.
/// </summary>
public sealed class Session
{
    private SessionStatus? _preNightStatus;
    private TimeSpan _preNightRemaining;

    public SessionStatus Status { get; private set; } = SessionStatus.Playing;
    public TimeSpan TimeRemaining { get; private set; } = TimeSpan.Zero;
    public ScheduleRule Rule { get; private set; } = ScheduleRule.Default;

    /// <summary>
    /// When false, the play/rest cadence is off: the computer is usable without limit
    /// (the timer never counts down and never enters a rest break). The night window
    /// still applies — it is enforced separately by the orchestrator.
    /// </summary>
    public bool IntervalsEnabled { get; private set; } = true;

    public Session()
    {
        TimeRemaining = Rule.PlayDuration;
    }

    private Session(SessionStatus status, TimeSpan timeRemaining, ScheduleRule rule, bool intervalsEnabled)
    {
        Status = status;
        TimeRemaining = timeRemaining < TimeSpan.Zero ? TimeSpan.Zero : timeRemaining;
        Rule = rule;
        IntervalsEnabled = intervalsEnabled;
    }

    /// <summary>Rehydrates a session from persisted values without running transitions.</summary>
    public static Session Restore(SessionStatus status, TimeSpan timeRemaining, ScheduleRule rule, bool intervalsEnabled = true)
        => new(status, timeRemaining, rule, intervalsEnabled);

    /// <summary>Turns the play/rest limit off — unlimited use (night block still applies).</summary>
    public void DisableIntervals()
    {
        IntervalsEnabled = false;
        if (Status is SessionStatus.Resting)
        {
            Status = SessionStatus.Playing;
        }
    }

    /// <summary>Turns the play/rest limit back on and restarts a fresh play phase.</summary>
    public void EnableIntervals()
    {
        IntervalsEnabled = true;
        if (Status is not (SessionStatus.ForceBlocked or SessionStatus.NightBlocked or SessionStatus.Paused))
        {
            Status = SessionStatus.Playing;
            TimeRemaining = Rule.PlayDuration;
        }
    }

    /// <summary>Advances the timer. Only <see cref="SessionStatus.Playing"/> and
    /// <see cref="SessionStatus.Resting"/> consume time; other states are frozen.</summary>
    public void Tick(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Elapsed time cannot be negative.");
        }

        if (elapsed == TimeSpan.Zero ||
            !IntervalsEnabled ||
            Status is SessionStatus.ForceBlocked or SessionStatus.NightBlocked or SessionStatus.Paused)
        {
            return;
        }

        var remaining = elapsed;
        while (remaining > TimeSpan.Zero)
        {
            var phase = Status switch
            {
                SessionStatus.Playing => Rule.PlayDuration,
                SessionStatus.Resting => Rule.RestDuration,
                _ => TimeSpan.Zero
            };

            if (phase <= TimeSpan.Zero)
            {
                TimeRemaining = TimeSpan.Zero;
                return;
            }

            if (TimeRemaining <= TimeSpan.Zero)
            {
                TimeRemaining = phase;
            }

            if (remaining < TimeRemaining)
            {
                TimeRemaining -= remaining;
                return;
            }

            remaining -= TimeRemaining;
            TimeRemaining = TimeSpan.Zero;
            Status = Status == SessionStatus.Playing ? SessionStatus.Resting : SessionStatus.Playing;
            TimeRemaining = Status == SessionStatus.Playing ? Rule.PlayDuration : Rule.RestDuration;
        }
    }

    public void AddTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Added time cannot be negative.");
        }

        TimeRemaining += time;
        if (Status == SessionStatus.Resting)
        {
            Status = SessionStatus.Playing;
        }
    }

    public void StartRest()
    {
        Status = SessionStatus.Resting;
        TimeRemaining = Rule.RestDuration;
    }

    public void ForceBlock() => Status = SessionStatus.ForceBlocked;

    public void ReleaseBlock()
    {
        Status = SessionStatus.Playing;
        if (TimeRemaining <= TimeSpan.Zero)
        {
            TimeRemaining = Rule.PlayDuration;
        }
    }

    public void Pause() => Status = SessionStatus.Paused;

    public void Resume()
    {
        if (Status == SessionStatus.Paused)
        {
            ReleaseBlock();
        }
    }

    public void ResetToPlayStart()
    {
        if (Status is SessionStatus.ForceBlocked or SessionStatus.NightBlocked)
        {
            return;
        }

        Status = SessionStatus.Playing;
        TimeRemaining = Rule.PlayDuration;
    }

    /// <summary>Enters the night block, snapshotting the prior state so it can be restored at dawn.</summary>
    public void EnterNight()
    {
        if (Status == SessionStatus.NightBlocked)
        {
            return;
        }

        _preNightStatus = Status;
        _preNightRemaining = TimeRemaining;
        Status = SessionStatus.NightBlocked;
        TimeRemaining = TimeSpan.Zero;
    }

    /// <summary>Exits the night block, restoring the snapshot taken by <see cref="EnterNight"/>.</summary>
    public void ExitNight()
    {
        if (Status != SessionStatus.NightBlocked)
        {
            return;
        }

        if (_preNightStatus is { } prior)
        {
            Status = prior;
            TimeRemaining = _preNightRemaining;
            _preNightStatus = null;
        }
        else
        {
            ReleaseBlock();
        }
    }

    public void ApplyRule(ScheduleRule rule)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
    }

    /// <summary>Applies a new rule and immediately resizes the current phase's timer.</summary>
    public void ApplyRuleResettingPhase(ScheduleRule rule)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        TimeRemaining = Status switch
        {
            SessionStatus.Playing => Rule.PlayDuration,
            SessionStatus.Resting => Rule.RestDuration,
            _ => TimeRemaining
        };
    }
}
