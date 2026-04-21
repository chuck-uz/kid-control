using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;

namespace KidControl.Domain.Entities;

public sealed class ComputerSession
{
    public LockStatus CurrentStatus { get; private set; } = LockStatus.Active;
    public TimeSpan TimeRemaining { get; private set; } = TimeSpan.Zero;
    public ScheduleRule? CurrentRule { get; private set; }

    public void Tick(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Elapsed time cannot be negative.");
        }

        if (elapsed == TimeSpan.Zero ||
            CurrentStatus == LockStatus.ForceBlocked ||
            CurrentStatus == LockStatus.NightBlock ||
            CurrentStatus == LockStatus.Paused)
        {
            return;
        }

        var remainingElapsed = elapsed;
        while (remainingElapsed > TimeSpan.Zero)
        {
            var phaseDuration = CurrentStatus == LockStatus.Active
                ? GetPlayDuration()
                : CurrentStatus == LockStatus.Blocked
                    ? GetRestDuration()
                    : TimeSpan.Zero;

            // Safety guard: if rule is invalid/missing, do not loop forever.
            if (phaseDuration <= TimeSpan.Zero)
            {
                TimeRemaining = TimeSpan.Zero;
                return;
            }

            if (TimeRemaining <= TimeSpan.Zero)
            {
                TimeRemaining = phaseDuration;
            }

            if (remainingElapsed < TimeRemaining)
            {
                TimeRemaining -= remainingElapsed;
                return;
            }

            remainingElapsed -= TimeRemaining;
            if (CurrentStatus == LockStatus.Active)
            {
                CurrentStatus = LockStatus.Blocked;
                TimeRemaining = GetRestDuration();
                continue;
            }

            if (CurrentStatus == LockStatus.Blocked)
            {
                CurrentStatus = LockStatus.Active;
                TimeRemaining = GetPlayDuration();
                continue;
            }

            return;
        }
    }

    public void AddTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Added time cannot be negative.");
        }

        TimeRemaining += time;

        if (CurrentStatus == LockStatus.Blocked)
        {
            CurrentStatus = LockStatus.Active;
        }
    }

    public void ForceBlock()
    {
        CurrentStatus = LockStatus.ForceBlocked;
    }

    public void BlockForRest()
    {
        CurrentStatus = LockStatus.Blocked;
        TimeRemaining = GetRestDuration();
    }

    public void ForceUnblock()
    {
        CurrentStatus = LockStatus.Active;

        if (TimeRemaining <= TimeSpan.Zero)
        {
            TimeRemaining = GetPlayDuration();
        }
    }

    public void SetNightBlock()
    {
        CurrentStatus = LockStatus.NightBlock;
        TimeRemaining = TimeSpan.Zero;
    }

    public void SetPaused()
    {
        CurrentStatus = LockStatus.Paused;
    }

    public void SetRule(ScheduleRule rule)
    {
        CurrentRule = rule ?? throw new ArgumentNullException(nameof(rule));
    }

    public void SetRuleAndResetPhase(ScheduleRule rule)
    {
        CurrentRule = rule ?? throw new ArgumentNullException(nameof(rule));

        TimeRemaining = CurrentStatus switch
        {
            LockStatus.Active => GetPlayDuration(),
            LockStatus.Blocked => GetRestDuration(),
            _ => TimeRemaining
        };
    }

    public void RestoreState(LockStatus status, TimeSpan timeRemaining)
    {
        if (timeRemaining < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeRemaining), "Time remaining cannot be negative.");
        }

        CurrentStatus = status;
        TimeRemaining = timeRemaining;
    }

    private TimeSpan GetPlayDuration()
    {
        return TimeSpan.FromMinutes(CurrentRule?.PlayMinutes ?? 0);
    }

    private TimeSpan GetRestDuration()
    {
        return TimeSpan.FromMinutes(CurrentRule?.RestMinutes ?? 0);
    }
}
