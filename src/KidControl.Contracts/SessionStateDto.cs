namespace KidControl.Contracts;

/// <summary>State snapshot pushed from the service to the UI over the state pipe.</summary>
public sealed record SessionStateDto(
    string Status,
    TimeSpan TimeRemaining,
    bool IsNightMode);
