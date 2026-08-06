namespace KidControl.Domain.ValueObjects;

/// <summary>
/// A nightly blocking window, e.g. 22:00–07:00. Correctly handles windows that
/// wrap past midnight. This logic used to live scattered inside the orchestrator;
/// here it is a pure, fully testable value object.
/// </summary>
public sealed record NightWindow
{
    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    public NightWindow(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || start >= TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "Start must be within a 24h day.");
        }

        if (end < TimeSpan.Zero || end >= TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "End must be within a 24h day.");
        }

        Start = start;
        End = end;
    }

    public static NightWindow Default { get; } = new(TimeSpan.FromHours(22), TimeSpan.FromHours(7));

    /// <summary>True if the given time-of-day falls inside the window.</summary>
    public bool Contains(TimeSpan timeOfDay)
    {
        // Degenerate window (start == end) is treated as "always night" — matches the
        // original behaviour where an unset/equal window blocked around the clock.
        if (Start == End)
        {
            return true;
        }

        return Start < End
            ? timeOfDay >= Start && timeOfDay < End
            : timeOfDay >= Start || timeOfDay < End;
    }

    /// <summary>The absolute moment the current night window ends, relative to <paramref name="now"/>.</summary>
    public DateTimeOffset NextEnd(DateTimeOffset now)
    {
        if (Start == End)
        {
            return new DateTimeOffset(now.Date.AddDays(1).Add(End), now.Offset);
        }

        if (Start < End)
        {
            return new DateTimeOffset(now.Date.Add(End), now.Offset);
        }

        var endDate = now.TimeOfDay < End ? now.Date : now.Date.AddDays(1);
        return new DateTimeOffset(endDate.Add(End), now.Offset);
    }

    /// <summary>Parses a "HH:mm-HH:mm" interval such as "21:30-08:00".</summary>
    public static bool TryParse(string? text, out NightWindow window)
    {
        window = Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !TimeSpan.TryParseExact(parts[0], @"hh\:mm", null, out var start) ||
            !TimeSpan.TryParseExact(parts[1], @"hh\:mm", null, out var end))
        {
            return false;
        }

        window = new NightWindow(start, end);
        return true;
    }

    public override string ToString() => $"{Start:hh\\:mm}-{End:hh\\:mm}";
}
