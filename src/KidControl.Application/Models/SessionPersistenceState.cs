using KidControl.Domain.Enums;

namespace KidControl.Application.Models;

public sealed record SessionPersistenceState
{
    public TimeSpan TimeRemaining { get; init; }
    public LockStatus CurrentStatus { get; init; }
    public DateTimeOffset LastUpdateTimestamp { get; init; }
    public int PlayMinutes { get; init; } = 40;
    public int RestMinutes { get; init; } = 20;
    public TimeSpan NightModeStart { get; init; } = TimeSpan.FromHours(22);
    public TimeSpan NightModeEnd { get; init; } = TimeSpan.FromHours(7);
}
