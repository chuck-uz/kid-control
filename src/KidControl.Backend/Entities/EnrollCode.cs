namespace KidControl.Backend.Entities;

/// <summary>
/// A one-time enrollment code (decision #6). An operator generates it from the bot; the
/// agent exchanges it at <c>/agent/enroll</c> for a per-device token. Single-use: once
/// <see cref="UsedByDeviceId"/> is set it can't be redeemed again, and it expires at
/// <see cref="ExpiresAt"/>.
/// </summary>
public sealed class EnrollCode
{
    /// <summary>The short human-typable code (the primary key).</summary>
    public string Code { get; set; } = string.Empty;

    public Guid TenantId { get; set; } = Tenant.DefaultId;
    public Tenant? Tenant { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Non-null once redeemed — the device it enrolled.</summary>
    public Guid? UsedByDeviceId { get; set; }

    public bool IsRedeemable(DateTimeOffset now) => UsedByDeviceId is null && now <= ExpiresAt;
}
