using KidControl.Domain.ValueObjects;

namespace KidControl.Fleet.Contracts;

/// <summary>
/// The declarative, versioned policy the backend owns and the agent caches + enforces.
/// Fields are primitives for tolerant deserialization; <see cref="ToScheduleRule"/> and
/// <see cref="ToNightWindow"/> project onto the validated domain value objects (the backend
/// validates before storing/sending, so agents only ever receive well-formed policies).
/// </summary>
public sealed record PolicyDto
{
    /// <summary>Monotonic per-device version; the agent sends the version it holds.</summary>
    public int Version { get; init; }

    public int PlayMinutes { get; init; } = 40;
    public int RestMinutes { get; init; } = 20;

    public bool NightEnabled { get; init; } = true;
    public TimeSpan NightStart { get; init; } = TimeSpan.FromHours(22);
    public TimeSpan NightEnd { get; init; } = TimeSpan.FromHours(7);

    public bool IntervalsEnabled { get; init; } = true;

    /// <summary>Desired update target: "latest" or a pinned tag like "v2.0.10".</summary>
    public string TargetVersion { get; init; } = "latest";

    public ScheduleRule ToScheduleRule() => new(PlayMinutes, RestMinutes);

    public NightWindow ToNightWindow() => new(NightStart, NightEnd);

    public static PolicyDto From(ScheduleRule rule, NightWindow night, int version,
        bool nightEnabled = true, bool intervalsEnabled = true, string targetVersion = "latest") => new()
    {
        Version = version,
        PlayMinutes = rule.PlayMinutes,
        RestMinutes = rule.RestMinutes,
        NightEnabled = nightEnabled,
        NightStart = night.Start,
        NightEnd = night.End,
        IntervalsEnabled = intervalsEnabled,
        TargetVersion = targetVersion
    };
}
