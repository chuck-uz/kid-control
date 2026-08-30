using KidControl.Fleet.Contracts;

namespace KidControl.Backend.Entities;

/// <summary>
/// The declarative, versioned policy for one device (§6 "synchronised state"). The backend
/// owns it; <see cref="Version"/> is monotonic per device and bumped on every change so a
/// stale agent gets a fresh snapshot on its next heartbeat.
/// </summary>
public sealed class DevicePolicy
{
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public int Version { get; set; } = 1;

    public int PlayMinutes { get; set; } = 40;
    public int RestMinutes { get; set; } = 20;

    public bool NightEnabled { get; set; } = true;
    public TimeSpan NightStart { get; set; } = TimeSpan.FromHours(22);
    public TimeSpan NightEnd { get; set; } = TimeSpan.FromHours(7);

    public bool IntervalsEnabled { get; set; } = true;

    /// <summary>Content monitor (RFC-05): keystroke + window/URL matching. Default ON.</summary>
    public bool WordMonitorEnabled { get; set; } = true;

    /// <summary>How many raw chars of context the alert carries around a match.</summary>
    public int MonitorContextChars { get; set; } = 30;

    /// <summary>Update target: "latest" or a pinned tag ("v2.0.10").</summary>
    public string TargetVersion { get; set; } = "latest";

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Project onto the wire contract the agent caches and enforces.
    /// <paramref name="monitorListsVersion"/> is the current backend list version so the agent
    /// knows when to re-fetch the (large) lists from <c>/agent/monitor-lists</c>.</summary>
    public PolicyDto ToDto(int monitorListsVersion = 0) => new()
    {
        Version = Version,
        PlayMinutes = PlayMinutes,
        RestMinutes = RestMinutes,
        NightEnabled = NightEnabled,
        NightStart = NightStart,
        NightEnd = NightEnd,
        IntervalsEnabled = IntervalsEnabled,
        TargetVersion = TargetVersion,
        WordMonitorEnabled = WordMonitorEnabled,
        MonitorContextChars = MonitorContextChars,
        MonitorListsVersion = monitorListsVersion
    };
}
