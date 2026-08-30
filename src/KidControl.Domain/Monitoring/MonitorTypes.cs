namespace KidControl.Domain.Monitoring;

/// <summary>What kind of content a monitor hit represents.</summary>
public enum MonitorCategory
{
    /// <summary>Profanity / obscene language (🤬).</summary>
    Profanity,

    /// <summary>Adult content — porn/erotica domains or keywords (🔞).</summary>
    Adult
}

/// <summary>Where a monitor hit was observed.</summary>
public enum MonitorSource
{
    /// <summary>Typed on the keyboard.</summary>
    Keyboard,

    /// <summary>The title of the active window / app.</summary>
    Window,

    /// <summary>A browser URL (host or the URL text).</summary>
    Url
}

/// <summary>
/// One content-monitor hit: the category, the matched term (a banned word or a domain),
/// the source it came from, and a short human-readable context (raw text tail, window
/// title, or host). Pure data — carries no raw keystroke buffer.
/// </summary>
public sealed record MonitorHit(MonitorCategory Category, string Term, MonitorSource Source, string Context);
