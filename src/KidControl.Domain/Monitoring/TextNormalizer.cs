using System.Text;

namespace KidControl.Domain.Monitoring;

/// <summary>
/// Canonicalises text so obfuscated variants collapse onto the same form for substring
/// matching. Deliberately aggressive (recall over precision — see RFC-05): the SAME
/// normaliser is applied to both the list terms and the observed text, so absolute
/// transliteration fidelity does not matter — only self-consistency does.
///
/// Pipeline (single pass): lowercase → fold confusables (ё→е, digit/latin leet → cyrillic)
/// → drop everything that is not a cyrillic letter or digit (so spaces, dots and dashes
/// between letters vanish: "с.у.к.а" → "сука") → collapse runs of the same char
/// ("бляяя" → "бля").
/// </summary>
public static class TextNormalizer
{
    // Confusable folding, applied after ToLowerInvariant. Latin letters are folded to their
    // visual/phonetic cyrillic counterpart so "cyka" → "сука"; a few digits stand in for
    // cyrillic letters in Russian leet. Unmapped chars pass through and are then filtered.
    private static readonly Dictionary<char, char> Fold = new()
    {
        ['ё'] = 'е', ['@'] = 'а',
        // Russian-leet digits.
        ['0'] = 'о', ['3'] = 'е', ['6'] = 'б', ['9'] = 'я',
        // Latin → cyrillic (translit / visual look-alikes).
        ['a'] = 'а', ['b'] = 'б', ['c'] = 'с', ['d'] = 'д', ['e'] = 'е', ['f'] = 'ф',
        ['g'] = 'г', ['h'] = 'х', ['i'] = 'и', ['j'] = 'ж', ['k'] = 'к', ['l'] = 'л',
        ['m'] = 'м', ['n'] = 'н', ['o'] = 'о', ['p'] = 'п', ['q'] = 'к', ['r'] = 'р',
        ['s'] = 'с', ['t'] = 'т', ['u'] = 'у', ['v'] = 'в', ['w'] = 'в', ['x'] = 'х',
        ['y'] = 'у', ['z'] = 'з',
    };

    private static bool IsAllowed(char c) => c is >= 'а' and <= 'я' or >= '0' and <= '9';

    /// <summary>Returns the canonical form of <paramref name="raw"/> (never null).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        var last = '\0';
        foreach (var ch0 in raw)
        {
            var ch = char.ToLowerInvariant(ch0);
            if (Fold.TryGetValue(ch, out var folded))
            {
                ch = folded;
            }

            if (!IsAllowed(ch) || ch == last)
            {
                continue; // drop separators/unknowns, and collapse immediate repeats
            }

            sb.Append(ch);
            last = ch;
        }

        return sb.ToString();
    }
}
