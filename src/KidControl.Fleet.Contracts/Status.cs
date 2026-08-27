namespace KidControl.Fleet.Contracts;

/// <summary>
/// The agent's live session state, reported up on each heartbeat purely for display in the
/// bot/dashboard. The agent — not the backend — owns this (it runs the clock locally).
/// Mirrors the existing <c>SessionStateDto</c> plus a couple of fleet fields.
/// </summary>
public sealed record StatusReportDto
{
    public string Status { get; init; } = "Playing";
    public TimeSpan TimeRemaining { get; init; }
    public bool IsNight { get; init; }
    public bool IsUnlimited { get; init; }
    public int ShutdownInSeconds { get; init; } = -1;
    public string? AgentVersion { get; init; }
}
