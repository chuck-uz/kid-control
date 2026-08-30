namespace KidControl.Fleet.Contracts;

/// <summary>
/// The content-monitor lists (RFC-05), delivered to the agent from <c>/agent/monitor-lists</c>
/// on demand (when <see cref="Version"/> is newer than the agent's cached copy) — not in every
/// heartbeat, because the adult-domain list can be tens of thousands of rows.
/// </summary>
public sealed record MonitorListsDto
{
    public int Version { get; init; }
    public IReadOnlyList<string> Profanity { get; init; } = [];
    public IReadOnlyList<string> AdultKeywords { get; init; } = [];
    public IReadOnlyList<string> AdultDomains { get; init; } = [];
    public IReadOnlyList<string> Exceptions { get; init; } = [];
}

/// <summary>
/// One content-monitor hit the agent pushes to <c>/agent/alert</c> the moment it happens
/// (RFC-05). The screenshot travels as the multipart file part alongside this JSON; the raw
/// input buffer never leaves the device.
/// </summary>
public sealed record WordAlertDto
{
    /// <summary>"profanity" or "adult".</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>The matched word or domain.</summary>
    public string Term { get; init; } = string.Empty;

    /// <summary>"keyboard", "window" or "url".</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Short human-readable context (raw buffer tail, window title, or host).</summary>
    public string Context { get; init; } = string.Empty;
}
