using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Fleet;

public enum EnrollError { None, InvalidCode, Expired, AlreadyUsed }

/// <summary>Result of redeeming an enroll code. On success carries the one-time token.</summary>
public sealed record EnrollResult(EnrollError Error, EnrollResponse? Response)
{
    public bool Ok => Error == EnrollError.None;
    public static EnrollResult Fail(EnrollError e) => new(e, null);
    public static EnrollResult Success(Guid deviceId, string token) =>
        new(EnrollError.None, new EnrollResponse(deviceId.ToString(), token));
}

public sealed record NewCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Owns enrollment (§5.1): an operator mints a single-use code; an agent redeems it for a
/// per-device bearer token. Only the token's hash is stored. Redeeming also provisions the
/// device's default policy/desired rows so the very first heartbeat has state to sync.
/// </summary>
public sealed class EnrollmentService(FleetDbContext db, TimeProvider clock)
{
    /// <summary>Mint a single-use code (default TTL 30 min). Returns the display form.</summary>
    public async Task<NewCode> CreateCodeAsync(TimeSpan? ttl = null, string actor = "system",
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var expires = now + (ttl ?? TimeSpan.FromMinutes(30));

        // Store the normalized (dash-free) code as the PK; hand back the readable display form.
        string display, normalized;
        do
        {
            display = FleetTokens.NewEnrollCode();
            normalized = FleetTokens.NormalizeCode(display);
        }
        while (await db.EnrollCodes.AnyAsync(c => c.Code == normalized, ct));

        db.EnrollCodes.Add(new EnrollCode
        {
            Code = normalized,
            TenantId = Tenant.DefaultId,
            CreatedAt = now,
            ExpiresAt = expires
        });
        db.Audits.Add(Audit(actor, "enroll_code.create", null, $"{{\"expiresAt\":\"{expires:O}\"}}", now));
        await db.SaveChangesAsync(ct);

        return new NewCode(display, expires);
    }

    /// <summary>Redeem a code: create the device, issue its token, mark the code used.</summary>
    public async Task<EnrollResult> EnrollAsync(EnrollRequest req, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var normalized = FleetTokens.NormalizeCode(req.Code ?? string.Empty);

        // Serializable so two agents can't redeem the same code concurrently.
        await using var tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);

        var code = await db.EnrollCodes.FirstOrDefaultAsync(c => c.Code == normalized, ct);
        if (code is null)
            return EnrollResult.Fail(EnrollError.InvalidCode);
        if (code.UsedByDeviceId is not null)
            return EnrollResult.Fail(EnrollError.AlreadyUsed);
        if (now > code.ExpiresAt)
            return EnrollResult.Fail(EnrollError.Expired);

        var token = FleetTokens.NewDeviceToken();
        var device = new Device
        {
            TenantId = code.TenantId,
            Name = string.IsNullOrWhiteSpace(req.MachineName) ? "Устройство" : req.MachineName.Trim(),
            EnrolledAt = now,
            LastSeenAt = now,
            AgentVersion = req.AgentVersion,
            OsInfo = req.OsInfo,
            TokenHash = FleetTokens.HashToken(token),
            Revoked = false
        };
        db.Devices.Add(device);

        // Provision default state so the first heartbeat has something to reconcile against.
        db.DevicePolicies.Add(new DevicePolicy { DeviceId = device.Id, Version = 1, UpdatedAt = now });
        db.DeviceDesired.Add(new DeviceDesired { DeviceId = device.Id, Version = 1, UpdatedAt = now });

        code.UsedByDeviceId = device.Id;
        db.Audits.Add(Audit("agent", "device.enroll", device.Id,
            $"{{\"machineName\":{JsonString(device.Name)}}}", now));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return EnrollResult.Success(device.Id, token);
    }

    private static Audit Audit(string actor, string action, Guid? deviceId, string detailJson,
        DateTimeOffset at) => new()
    {
        TenantId = Tenant.DefaultId,
        Actor = actor,
        Action = action,
        DeviceId = deviceId,
        DetailJson = detailJson,
        At = at
    };

    private static string JsonString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
