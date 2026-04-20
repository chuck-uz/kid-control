namespace KidControl.Domain.ValueObjects;

public sealed record ScheduleRule
{
    public int PlayMinutes { get; }
    public int RestMinutes { get; }

    public ScheduleRule(int playMinutes, int restMinutes)
    {
        if (playMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playMinutes), "Play minutes cannot be negative.");
        }

        if (restMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(restMinutes), "Rest minutes cannot be negative.");
        }

        PlayMinutes = playMinutes;
        RestMinutes = restMinutes;
    }
}
