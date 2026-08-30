namespace KidControl.Backend.Entities;

/// <summary>Which content-monitor list a term belongs to (RFC-05).</summary>
public static class MonitorTermKind
{
    public const string Profanity = "profanity";
    public const string AdultKeyword = "adult_keyword";
    public const string AdultDomain = "adult_domain";
    public const string Exception = "exception";
}

/// <summary>
/// One entry of a content-monitor list (RFC-05). Lists are backend-owned and delivered to the
/// agent on demand (versioned) rather than in every heartbeat, because the adult-domain list
/// can be tens of thousands of rows.
/// </summary>
public sealed class MonitorTerm
{
    public int Id { get; set; }

    /// <summary>One of <see cref="MonitorTermKind"/>.</summary>
    public string Kind { get; set; } = MonitorTermKind.Profanity;

    /// <summary>The raw term (word or domain); normalisation happens on the agent.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Single-row table holding the monotonically increasing version of the monitor lists. The
/// agent caches the lists and re-fetches only when the version it holds is behind.
/// </summary>
public sealed class MonitorMeta
{
    /// <summary>Fixed at 1 — there is exactly one row.</summary>
    public int Id { get; set; } = 1;

    public int ListsVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
