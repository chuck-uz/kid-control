namespace KidControl.Domain;

public sealed record SessionPolicy(
    TimeSpan DailyLimit,
    TimeSpan MaxSessionLength);
