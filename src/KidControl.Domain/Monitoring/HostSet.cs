namespace KidControl.Domain.Monitoring;

/// <summary>
/// A set of blocked domains matched by host suffix: a host matches a listed domain when it
/// equals it or is a subdomain of it ("www.pornhub.com" and "cdn.pornhub.com" both match
/// "pornhub.com"). Domains are lowercased and a leading "www." is stripped at construction.
/// Lookup is O(number of host labels), not O(list size).
/// </summary>
public sealed class HostSet
{
    private readonly HashSet<string> _domains;

    public HostSet(IEnumerable<string> domains)
    {
        _domains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in domains ?? Enumerable.Empty<string>())
        {
            var clean = Clean(d);
            if (clean.Length > 0)
            {
                _domains.Add(clean);
            }
        }
    }

    public int Count => _domains.Count;

    private static string Clean(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return string.Empty;
        }

        var h = d.Trim().ToLowerInvariant().Trim('.');
        if (h.StartsWith("www.", StringComparison.Ordinal))
        {
            h = h["www.".Length..];
        }
        return h;
    }

    /// <summary>Extracts the lowercased host from a URL (or a bare host). Empty if unparseable.</summary>
    public static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var text = url.Trim();
        var withScheme = text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;
        if (Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            return uri.Host.ToLowerInvariant();
        }

        // Fallback: strip scheme, path, query, port manually.
        var h = text;
        var scheme = h.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            h = h[(scheme + 3)..];
        }
        h = h.Split('/', '?', '#')[0].Split('@')[^1].Split(':')[0];
        return h.ToLowerInvariant();
    }

    /// <summary>True if <paramref name="host"/> equals or is a subdomain of a listed domain.</summary>
    public bool TryMatch(string? host, out string domain)
    {
        domain = string.Empty;
        var h = host?.Trim().ToLowerInvariant().Trim('.') ?? string.Empty;
        if (h.Length == 0 || _domains.Count == 0)
        {
            return false;
        }

        // Check the full host and each parent suffix: a.b.example.com → a.b.example.com,
        // b.example.com, example.com, com.
        var i = 0;
        while (true)
        {
            var candidate = i == 0 ? h : h[i..];
            if (_domains.Contains(candidate))
            {
                domain = candidate;
                return true;
            }

            var dot = h.IndexOf('.', i);
            if (dot < 0)
            {
                return false;
            }
            i = dot + 1;
        }
    }
}
