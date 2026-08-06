using KidControl.Domain.Enums;

namespace KidControl.Application.Models;

/// <summary>Serializable persistence model for the session (survives restarts).</summary>
public sealed record SessionSnapshot
{
    public TimeSpan TimeRemaining { get; init; }
    public SessionStatus Status { get; init; } = SessionStatus.Playing;
    public DateTimeOffset LastUpdated { get; init; }
    public int PlayMinutes { get; init; } = 40;
    public int RestMinutes { get; init; } = 20;
    public TimeSpan NightStart { get; init; } = TimeSpan.FromHours(22);
    public TimeSpan NightEnd { get; init; } = TimeSpan.FromHours(7);
}
