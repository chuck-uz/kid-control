namespace KidControl.Domain.Monitoring;

/// <summary>
/// A compiled set of terms matched as normalised substrings (see <see cref="TextNormalizer"/>).
/// Terms are normalised once at construction; empty/blank terms are dropped. Longer terms are
/// tried first so the most specific match wins and drives the alert label.
/// </summary>
public sealed class TermSet
{
    private readonly (string Norm, string Original)[] _terms;

    public TermSet(IEnumerable<string> rawTerms)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(string, string)>();
        foreach (var raw in rawTerms ?? Enumerable.Empty<string>())
        {
            var norm = TextNormalizer.Normalize(raw);
            if (norm.Length == 0 || !seen.Add(norm))
            {
                continue;
            }
            list.Add((norm, (raw ?? string.Empty).Trim()));
        }

        // Longest first: prefer the most specific term when several match.
        _terms = list.OrderByDescending(t => t.Item1.Length).ToArray();
    }

    public int Count => _terms.Length;

    /// <summary>The normalised forms of every term (used for exception suppression).</summary>
    public IEnumerable<string> Normalized => _terms.Select(t => t.Norm);

    /// <summary>All terms whose normalised form occurs in <paramref name="normalizedText"/>.</summary>
    public IEnumerable<(string Original, string Norm)> Matches(string normalizedText)
    {
        if (string.IsNullOrEmpty(normalizedText))
        {
            yield break;
        }

        foreach (var (norm, original) in _terms)
        {
            if (normalizedText.Contains(norm, StringComparison.Ordinal))
            {
                yield return (original, norm);
            }
        }
    }

    /// <summary>First (most specific) match, or false.</summary>
    public bool TryMatch(string normalizedText, out string original)
    {
        foreach (var m in Matches(normalizedText))
        {
            original = m.Original;
            return true;
        }

        original = string.Empty;
        return false;
    }
}
