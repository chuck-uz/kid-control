namespace KidControl.Domain.Monitoring;

/// <summary>
/// The pure matching core of the content monitor (RFC-05). Holds the compiled lists —
/// profanity words, adult keywords, adult domains, and exceptions — and decides whether a
/// piece of observed text or a URL is a hit. Fully clock-free and I/O-free, so it is unit
/// tested exhaustively; the agent wraps it with the keyboard hook and window/URL watcher.
///
/// Priority when several categories could match: adult domain → adult keyword → profanity.
/// A match is suppressed if an exception term that CONTAINS the matched term is present in the
/// same text (the classic false-positive fix: a banned root sitting inside an allowed word).
/// </summary>
public sealed class ContentMonitor
{
    private readonly TermSet _profanity;
    private readonly TermSet _adultKeywords;
    private readonly HostSet _adultDomains;
    private readonly TermSet _exceptions;

    public ContentMonitor(
        IEnumerable<string> profanity,
        IEnumerable<string> adultKeywords,
        IEnumerable<string> adultDomains,
        IEnumerable<string>? exceptions = null)
    {
        _profanity = new TermSet(profanity);
        _adultKeywords = new TermSet(adultKeywords);
        _adultDomains = new HostSet(adultDomains);
        _exceptions = new TermSet(exceptions ?? Enumerable.Empty<string>());
    }

    /// <summary>An empty monitor (nothing configured yet) never hits.</summary>
    public static ContentMonitor Empty { get; } =
        new(Enumerable.Empty<string>(), Enumerable.Empty<string>(), Enumerable.Empty<string>());

    public bool IsEmpty => _profanity.Count == 0 && _adultKeywords.Count == 0 && _adultDomains.Count == 0;

    /// <summary>
    /// Scans a piece of text (a keyboard buffer or a window title). Adult keywords take
    /// priority over profanity. <paramref name="context"/> is the human-readable snippet the
    /// alert will carry (the caller supplies e.g. the raw buffer tail or the window title).
    /// </summary>
    public MonitorHit? ScanText(string? text, MonitorSource source, string context)
    {
        var norm = TextNormalizer.Normalize(text);
        if (norm.Length == 0)
        {
            return null;
        }

        foreach (var (original, matchedNorm) in _adultKeywords.Matches(norm))
        {
            if (!IsSuppressed(norm, matchedNorm))
            {
                return new MonitorHit(MonitorCategory.Adult, original, source, context);
            }
        }

        foreach (var (original, matchedNorm) in _profanity.Matches(norm))
        {
            if (!IsSuppressed(norm, matchedNorm))
            {
                return new MonitorHit(MonitorCategory.Profanity, original, source, context);
            }
        }

        return null;
    }

    /// <summary>
    /// Scans a URL by its HOST against the adult-domain list only. URLs are deliberately NOT
    /// keyword-scanned: short substrings (e.g. "xxx", "sex") occur inside innocuous paths, query
    /// tokens and hostnames ("essex.com", CDN ids), which produced many false positives. Adult
    /// queries are still caught on the keyboard and in the window title, where matching text is
    /// human-readable.
    /// </summary>
    public MonitorHit? ScanUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var host = HostSet.HostOf(url);
        if (_adultDomains.TryMatch(host, out var domain))
        {
            return new MonitorHit(MonitorCategory.Adult, domain, MonitorSource.Url, host.Length > 0 ? host : url);
        }

        return null;
    }

    private bool IsSuppressed(string normalizedText, string matchedNorm)
    {
        foreach (var ex in _exceptions.Normalized)
        {
            if (ex.Contains(matchedNorm, StringComparison.Ordinal) &&
                normalizedText.Contains(ex, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
