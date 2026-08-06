namespace KidControl.Domain.ValueObjects;

/// <summary>
/// Immutable play/rest cadence. Both values must be positive.
/// </summary>
public sealed record ScheduleRule
{
    public const int MaxMinutes = 24 * 60;

    public int PlayMinutes { get; }
    public int RestMinutes { get; }

    public ScheduleRule(int playMinutes, int restMinutes)
    {
        if (playMinutes is <= 0 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playMinutes), playMinutes, $"Play minutes must be between 1 and {MaxMinutes}.");
        }

        if (restMinutes is <= 0 or > MaxMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(restMinutes), restMinutes, $"Rest minutes must be between 1 and {MaxMinutes}.");
        }

        PlayMinutes = playMinutes;
        RestMinutes = restMinutes;
    }

    public TimeSpan PlayDuration => TimeSpan.FromMinutes(PlayMinutes);
    public TimeSpan RestDuration => TimeSpan.FromMinutes(RestMinutes);

    public static ScheduleRule Default { get; } = new(playMinutes: 40, restMinutes: 20);

    /// <summary>Tries to parse a "play/rest" string such as "50/10".</summary>
    public static bool TryParse(string? text, out ScheduleRule rule)
    {
        rule = Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var play) ||
            !int.TryParse(parts[1], out var rest) ||
            play is <= 0 or > MaxMinutes ||
            rest is <= 0 or > MaxMinutes)
        {
            return false;
        }

        rule = new ScheduleRule(play, rest);
        return true;
    }
}
