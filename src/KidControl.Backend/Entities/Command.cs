namespace KidControl.Backend.Entities;

/// <summary>
/// A one-shot imperative command (§6): TTL'd, delivered at-most-once, acked. Distinct from
/// policy/desired state — a command fires once and then lives on only as history. Lifecycle:
/// created → delivered (long-poll hands it out) → acked (agent reports the result).
/// </summary>
public sealed class Command
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    /// <summary>One of <see cref="Fleet.Contracts.CommandTypes"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Optional string→string arguments, stored as jsonb.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>After this moment the command is stale and must be dropped, not run.</summary>
    public DateTimeOffset TtlAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }

    /// <summary>Agent-reported outcome ("ok", or an error message).</summary>
    public string? Result { get; set; }
}
