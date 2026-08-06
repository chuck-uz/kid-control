using System.Security.Cryptography;
using KidControl.Application.Abstractions;

namespace KidControl.Application.Services;

/// <summary>
/// Issues and validates one-time emergency codes.
///
/// Hardening over the original (which had a 4-digit code, unlimited attempts, and
/// no lockout — brute-forceable over the open command pipe in seconds):
///  * 6-digit cryptographically-random code;
///  * fixed attempt budget, after which the active code is burned;
///  * time-boxed validity via <see cref="IClock"/> (now testable);
///  * per-issue lockout window to throttle re-issue spam.
/// </summary>
public sealed class EmergencyOtpService(IClock clock)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReissueCooldown = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private string? _code;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset _lastIssuedAt = DateTimeOffset.MinValue;
    private int _attemptsLeft;

    public enum ValidationResult { Valid, Invalid, NoActiveCode, Expired, LockedOut }

    /// <summary>Generates a fresh code, or returns null if a re-issue is still on cooldown.</summary>
    public string? TryIssue()
    {
        lock (_sync)
        {
            var now = clock.UtcNow;
            if (now - _lastIssuedAt < ReissueCooldown)
            {
                return null;
            }

            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            _code = code;
            _expiresAt = now + Validity;
            _lastIssuedAt = now;
            _attemptsLeft = MaxAttempts;
            return code;
        }
    }

    public ValidationResult Validate(string? candidate)
    {
        lock (_sync)
        {
            if (_code is null)
            {
                return ValidationResult.NoActiveCode;
            }

            if (clock.UtcNow > _expiresAt)
            {
                Burn();
                return ValidationResult.Expired;
            }

            if (_attemptsLeft <= 0)
            {
                Burn();
                return ValidationResult.LockedOut;
            }

            _attemptsLeft--;

            var supplied = (candidate ?? string.Empty).Trim();
            // Constant-time comparison to avoid leaking correctness via timing.
            var ok = supplied.Length == _code.Length &&
                     CryptographicOperations.FixedTimeEquals(
                         System.Text.Encoding.ASCII.GetBytes(supplied),
                         System.Text.Encoding.ASCII.GetBytes(_code));

            if (ok)
            {
                Burn();
                return ValidationResult.Valid;
            }

            if (_attemptsLeft <= 0)
            {
                Burn();
            }

            return ValidationResult.Invalid;
        }
    }

    private void Burn()
    {
        _code = null;
        _attemptsLeft = 0;
    }
}
