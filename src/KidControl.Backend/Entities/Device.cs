namespace KidControl.Backend.Entities;

/// <summary>
/// An enrolled agent machine. Identity is a per-device bearer token; only its hash is
/// stored (<see cref="TokenHash"/>), and <see cref="Revoked"/> lets an operator cut a
/// device off without deleting its history. The policy/desired/status rows hang off this
/// one via one-to-one navigations.
/// </summary>
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; } = Tenant.DefaultId;
    public Tenant? Tenant { get; set; }

    /// <summary>Human name shown in the bot (defaults to the reported machine name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional grouping label ("дети", "гостиная") for bot folders.</summary>
    public string? GroupLabel { get; set; }

    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    public string? AgentVersion { get; set; }
    public string? OsInfo { get; set; }

    /// <summary>SHA-256 (hex) of the device bearer token. Never store the token itself.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public bool Revoked { get; set; }

    public DevicePolicy? Policy { get; set; }
    public DeviceDesired? Desired { get; set; }
    public DeviceStatus? Status { get; set; }
    public ICollection<Command> Commands { get; set; } = new List<Command>();
}
