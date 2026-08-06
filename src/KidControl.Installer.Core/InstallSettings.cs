namespace KidControl.Installer.Core;

/// <summary>
/// Everything the operator supplies for a fresh install. Carries the bot token only
/// as long as it takes to serialize appsettings.json — it is never logged and never
/// surfaced through the progress callback.
/// </summary>
public sealed class InstallSettings
{
    public required string BotToken { get; init; }

    public required IReadOnlyList<long> AdminChatIds { get; init; }

    public TimeSpan NightStart { get; init; } = TimeSpan.FromHours(22);

    public TimeSpan NightEnd { get; init; } = TimeSpan.FromHours(7);

    // ─── Update trust (self-update runs as SYSTEM, so these gate RCE) ───────────
    public string UpdateOwner { get; init; } = "chuck-uz";

    public string UpdateRepository { get; init; } = "kid-control";

    public bool RequireSignature { get; init; } = true;

    /// <summary>SHA-256 thumbprint of the trusted publisher cert (uppercase hex, no spaces).</summary>
    public string? TrustedThumbprint { get; init; }

    // ─── Protection posture ────────────────────────────────────────────────────
    public bool CriticalProcess { get; init; }

    public bool ApplyProcessDacl { get; init; } = true;

    public bool TamperDetection { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BotToken))
        {
            throw new ArgumentException("Bot token is required.");
        }

        if (AdminChatIds.Count == 0)
        {
            throw new ArgumentException("At least one admin chat id is required.");
        }
    }
}

/// <summary>Inputs for a full install run.</summary>
public sealed class InstallRequest
{
    /// <summary>Directory containing the freshly built ServiceHost.exe + UiHost.exe payload.</summary>
    public required string SourceDirectory { get; init; }

    public required InstallSettings Settings { get; init; }
}

/// <summary>Inputs for a binary-only update (config + state preserved).</summary>
public sealed class UpdateRequest
{
    public required string SourceDirectory { get; init; }
}
