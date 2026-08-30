namespace KidControl.Backend.Entities;

/// <summary>
/// Metadata for one content-monitor alert (RFC-05, §8: "метаданные без контента"). We keep the
/// device, category, matched term, source and time — for the dashboard timeline/counts — but
/// NOT the surrounding context snippet or the screenshot, which are delivered only to the
/// operator's Telegram chat and never stored.
/// </summary>
public sealed class WordAlert
{
    public long Id { get; set; }

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>"profanity" or "adult".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The matched word or domain (no surrounding context).</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>"keyboard", "window" or "url".</summary>
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}
