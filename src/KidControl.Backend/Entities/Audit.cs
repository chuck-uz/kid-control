namespace KidControl.Backend.Entities;

/// <summary>
/// Append-only record of who did what — policy edits, overrides, commands, enroll/revoke.
/// Kept for the bot's history view and post-hoc "why did the device do X" questions.
/// </summary>
public sealed class Audit
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; } = Tenant.DefaultId;

    /// <summary>Who acted — a Telegram chat id, "system", or "agent:{deviceId}".</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Short verb, e.g. "policy.edit", "command.add_time", "device.revoke".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The device the action targeted, when applicable.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>Free-form JSON detail (before/after, payload), stored as jsonb.</summary>
    public string? DetailJson { get; set; }

    public DateTimeOffset At { get; set; }
}
