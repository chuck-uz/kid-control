using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Fleet;

/// <summary>Partial policy edit — only the provided fields change; the version is bumped once.</summary>
public sealed record PolicyPatch(
    int? PlayMinutes = null,
    int? RestMinutes = null,
    bool? NightEnabled = null,
    TimeSpan? NightStart = null,
    TimeSpan? NightEnd = null,
    bool? IntervalsEnabled = null,
    string? TargetVersion = null);

public sealed record DeviceSummary(
    Guid Id, string Name, string? GroupLabel, DateTimeOffset? LastSeenAt, string? AgentVersion,
    int PolicyVersion, string? Status, TimeSpan? TimeRemaining);

/// <summary>
/// Operator-side reads/edits over devices and their policy. Backs the temporary admin API
/// today; the Telegram bot (T11) will call the same methods. A policy edit bumps the
/// per-device version so the change propagates on the next heartbeat (§4).
/// </summary>
public sealed class DeviceAdminService(FleetDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(CancellationToken ct = default)
        => await db.Devices.AsNoTracking()
            .Where(d => !d.Revoked)
            .OrderBy(d => d.Name)
            .Select(d => new DeviceSummary(
                d.Id, d.Name, d.GroupLabel, d.LastSeenAt, d.AgentVersion,
                d.Policy!.Version, d.Status!.Status, d.Status.TimeRemaining))
            .ToListAsync(ct);

    /// <summary>Apply a partial policy edit; returns the new version, or null if device unknown.</summary>
    public async Task<int?> UpdatePolicyAsync(Guid deviceId, PolicyPatch patch, string actor = "operator",
        CancellationToken ct = default)
    {
        var policy = await db.DevicePolicies.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (policy is null)
            return null;

        if (patch.PlayMinutes is { } play && play > 0) policy.PlayMinutes = play;
        if (patch.RestMinutes is { } rest && rest > 0) policy.RestMinutes = rest;
        if (patch.NightEnabled is { } ne) policy.NightEnabled = ne;
        if (patch.NightStart is { } ns) policy.NightStart = ns;
        if (patch.NightEnd is { } end) policy.NightEnd = end;
        if (patch.IntervalsEnabled is { } ie) policy.IntervalsEnabled = ie;
        if (!string.IsNullOrWhiteSpace(patch.TargetVersion)) policy.TargetVersion = patch.TargetVersion;

        policy.Version += 1;
        policy.UpdatedAt = clock.GetUtcNow();

        db.Audits.Add(new Audit
        {
            TenantId = Tenant.DefaultId,
            Actor = actor,
            Action = "policy.edit",
            DeviceId = deviceId,
            DetailJson = $"{{\"version\":{policy.Version}}}",
            At = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
        return policy.Version;
    }
}
