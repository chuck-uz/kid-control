namespace KidControl.Infrastructure.Configuration;

public sealed class UpdateConfig
{
    public const string SectionName = "Update";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true, a newer verified release is downloaded and installed automatically (the
    /// service restarts on the new version, preserving config and the child's timer). When
    /// false, admins are only notified over Telegram. Auto-install still enforces the
    /// signature/thumbprint checks, so an unsigned or untrusted release is never run.
    /// </summary>
    public bool AutoInstall { get; init; } = true;

    /// <summary>GitHub owner. Fixed at deploy time — NOT taken from an attacker-writable source.</summary>
    public string Owner { get; init; } = "chuck-uz";

    public string Repository { get; init; } = "kid-control";

    /// <summary>
    /// When true, a downloaded installer must carry a valid Authenticode signature whose
    /// certificate thumbprint matches <see cref="TrustedThumbprint"/> before it is executed.
    /// Defaults to true — the self-update path runs as SYSTEM, so unverified execution is a
    /// remote-code-execution vector.
    /// </summary>
    public bool RequireSignature { get; init; } = true;

    /// <summary>SHA-256 thumbprint of the trusted publisher certificate (uppercase hex, no spaces).</summary>
    public string? TrustedThumbprint { get; init; }

    /// <summary>
    /// GitHub token for reading releases/assets from a PRIVATE repository. Leave empty for a
    /// public repo. Stored in the protected %ProgramData% appsettings; prefer a fine-grained
    /// token limited to Contents:Read on this one repo.
    /// </summary>
    public string? GitHubToken { get; init; }

    /// <summary>Optional allow-list of hosts a release asset may be downloaded from.</summary>
    public string[] AllowedAssetHosts { get; init; } = ["github.com", "objects.githubusercontent.com"];

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);
}
