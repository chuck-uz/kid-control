namespace KidControl.Contracts;

public sealed record SessionStateDto(string Status, TimeSpan TimeRemaining, bool IsNightMode = false);
