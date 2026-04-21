using KidControl.Domain.Enums;

namespace KidControl.Application.Models;

public sealed record SessionPersistenceState(
    TimeSpan TimeRemaining,
    LockStatus CurrentStatus,
    DateTimeOffset LastUpdateTimestamp,
    int PlayMinutes = 40,
    int RestMinutes = 20);
