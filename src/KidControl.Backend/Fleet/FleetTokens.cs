using System.Security.Cryptography;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Secret material for enrollment. Enroll codes are short and human-typable (an operator
/// reads one out); device tokens are long, opaque, and only ever stored as a SHA-256 hash
/// — the plaintext is returned once at enroll and never persisted.
/// </summary>
public static class FleetTokens
{
    // Crockford-ish base32 without I/O/L/U to avoid look-alikes when typed by hand.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A short single-use enrollment code, e.g. "K7Q2-9F3M" (8 chars, dashed).</summary>
    public static string NewEnrollCode(int chars = 8)
    {
        Span<char> buf = stackalloc char[chars];
        for (var i = 0; i < chars; i++)
            buf[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        var s = new string(buf);
        // Group into 4s for readability: "K7Q29F3M" -> "K7Q2-9F3M".
        return chars == 8 ? $"{s[..4]}-{s[4..]}" : s;
    }

    /// <summary>Normalise operator input: uppercase, strip spaces/dashes for lookup.</summary>
    public static string NormalizeCode(string code)
        => code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

    /// <summary>A long opaque bearer token (256 bits, base64url, no padding).</summary>
    public static string NewDeviceToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>SHA-256 of the token as lowercase hex (64 chars) — what we store/compare.</summary>
    public static string HashToken(string token)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
