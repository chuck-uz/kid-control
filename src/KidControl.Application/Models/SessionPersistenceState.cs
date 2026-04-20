using KidControl.Domain.Enums;

namespace KidControl.Application.Models;

public sealed record SessionPersistenceState(
    TimeSpan TimeRemaining,
    LockStatus CurrentStatus,
    DateTimeOffset LastUpdateTimestamp);
