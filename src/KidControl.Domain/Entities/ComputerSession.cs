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

        if (elapsed == TimeSpan.Zero || CurrentStatus == LockStatus.ForceBlocked)
        {
            return;
        }

        TimeRemaining -= elapsed;

        if (CurrentStatus == LockStatus.Active && TimeRemaining <= TimeSpan.Zero)
        {
            CurrentStatus = LockStatus.Blocked;
            TimeRemaining = GetRestDuration();
            return;
        }

        if (CurrentStatus == LockStatus.Blocked && TimeRemaining <= TimeSpan.Zero)
        {
            CurrentStatus = LockStatus.Active;
            TimeRemaining = GetPlayDuration();
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

    private TimeSpan GetPlayDuration()
    {
        return TimeSpan.FromMinutes(CurrentRule?.PlayMinutes ?? 0);
    }

    private TimeSpan GetRestDuration()
    {
        return TimeSpan.FromMinutes(CurrentRule?.RestMinutes ?? 0);
    }
}
