using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Verifies a downloaded installer before it is executed as SYSTEM: it must carry a valid
/// Authenticode signature whose signer certificate chains to a trusted root, and whose
/// SHA-256 certificate thumbprint matches the pinned publisher thumbprint. Also exposes a
/// SHA-256 file-hash helper for optional content pinning.
/// </summary>
public sealed class AuthenticodeVerifier(ILogger<AuthenticodeVerifier> logger)
{
    /// <summary>
    /// True if <paramref name="filePath"/> is Authenticode-signed, the signer chain is
    /// valid, and the signer's SHA-256 thumbprint equals <paramref name="expectedThumbprint"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public bool VerifySignature(string filePath, string? expectedThumbprint)
    {
        if (string.IsNullOrWhiteSpace(expectedThumbprint))
        {
            logger.LogWarning("Signature check requested but no trusted thumbprint configured.");
            return false;
        }

        try
        {
            using var signerCert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            if (!chain.Build(signerCert))
            {
                var reasons = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                logger.LogWarning("Authenticode chain invalid for {Path}: {Reasons}", filePath, reasons);
                return false;
            }

            var actual = ComputeCertThumbprint(signerCert);
            var expected = Normalize(expectedThumbprint);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                logger.LogWarning("Signer thumbprint mismatch for {Path}. Expected {Expected}, got {Actual}.",
                    filePath, expected, actual);
                return false;
            }

            return true;
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "File is not Authenticode-signed or signature is unreadable: {Path}", filePath);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Authenticode verification failed for {Path}.", filePath);
            return false;
        }
    }

    /// <summary>Uppercase-hex SHA-256 of the file's bytes.</summary>
    public async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string ComputeCertThumbprint(X509Certificate2 cert)
        => Convert.ToHexString(SHA256.HashData(cert.RawData));

    private static string Normalize(string thumbprint)
        => thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}
