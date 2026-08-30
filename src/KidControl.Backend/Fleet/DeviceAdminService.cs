using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Domain.ValueObjects;
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
    string? TargetVersion = null,
    bool? WordMonitorEnabled = null,
    int? MonitorContextChars = null);

public sealed record DeviceSummary(
    Guid Id, string Name, string? GroupLabel, DateTimeOffset? LastSeenAt, string? AgentVersion,
    int PolicyVersion, string? Status, TimeSpan? TimeRemaining, bool IsUnlimited = false, bool IsNight = false,
    bool Paused = false, bool ForceBlocked = false, string? TargetVersion = null,
    int PlayMinutes = 40, int RestMinutes = 20, bool IntervalsEnabled = true,
    bool WordMonitorEnabled = true,
    bool NightEnabled = true, TimeSpan NightStart = default, TimeSpan NightEnd = default);

/// <summary>One audit line for the bot's device history.</summary>
public sealed record AuditEntry(string Action, string Actor, DateTimeOffset At);

/// <summary>Active-use seconds for one local day (dashboard usage chart).</summary>
public sealed record UsageDay(DateOnly Day, long Seconds);

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
                d.Policy!.Version, d.Status!.Status, d.Status.TimeRemaining,
                d.Status != null && d.Status.IsUnlimited, d.Status != null && d.Status.IsNight,
                d.Desired != null && d.Desired.Paused, d.Desired != null && d.Desired.ForceBlocked,
                d.Policy!.TargetVersion,
                d.Policy.PlayMinutes, d.Policy.RestMinutes, d.Policy.IntervalsEnabled,
                d.Policy.WordMonitorEnabled,
                d.Policy.NightEnabled, d.Policy.NightStart, d.Policy.NightEnd))
            .ToListAsync(ct);

    /// <summary>Recent audit lines for a device, newest first.</summary>
    public async Task<IReadOnlyList<AuditEntry>> GetHistoryAsync(Guid deviceId, int limit = 15,
        CancellationToken ct = default)
        => await db.Audits.AsNoTracking()
            .Where(a => a.DeviceId == deviceId)
            .OrderByDescending(a => a.At)
            .Take(limit)
            .Select(a => new AuditEntry(a.Action, a.Actor, a.At))
            .ToListAsync(ct);

    /// <summary>
    /// Active-use seconds per local (Tashkent) day for the last <paramref name="days"/> days,
    /// oldest first, with missing days filled as zero so the chart has a continuous axis.
    /// </summary>
    public async Task<IReadOnlyList<UsageDay>> GetUsageAsync(Guid deviceId, int days, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5)).DateTime);
        var from = today.AddDays(-(days - 1));

        var byDay = await db.DeviceUsage.AsNoTracking()
            .Where(u => u.DeviceId == deviceId && u.Day >= from && u.Day <= today)
            .ToDictionaryAsync(u => u.Day, u => u.Seconds, ct);

        var result = new List<UsageDay>(days);
        for (var d = from; d <= today; d = d.AddDays(1))
            result.Add(new UsageDay(d, byDay.TryGetValue(d, out var s) ? s : 0));
        return result;
    }

    public async Task<DeviceSummary?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => await db.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId && !d.Revoked)
            .Select(d => new DeviceSummary(
                d.Id, d.Name, d.GroupLabel, d.LastSeenAt, d.AgentVersion,
                d.Policy!.Version, d.Status!.Status, d.Status.TimeRemaining,
                d.Status != null && d.Status.IsUnlimited, d.Status != null && d.Status.IsNight,
                d.Desired != null && d.Desired.Paused, d.Desired != null && d.Desired.ForceBlocked,
                d.Policy!.TargetVersion,
                d.Policy.PlayMinutes, d.Policy.RestMinutes, d.Policy.IntervalsEnabled,
                d.Policy.WordMonitorEnabled,
                d.Policy.NightEnabled, d.Policy.NightStart, d.Policy.NightEnd))
            .FirstOrDefaultAsync(ct);

    /// <summary>Give a device a friendly name (max 200 chars). Returns false if unknown/blank.</summary>
    public async Task<bool> RenameAsync(Guid deviceId, string name, string actor = "operator",
        CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return false;
        if (trimmed.Length > 200)
            trimmed = trimmed[..200];

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.Revoked, ct);
        if (device is null)
            return false;

        device.Name = trimmed;
        db.Audits.Add(new Audit
        {
            TenantId = Tenant.DefaultId, Actor = actor, Action = "device.rename",
            DeviceId = deviceId, DetailJson = $"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(trimmed)}}}",
            At = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Revoke a device (cuts its token off) without deleting its history.</summary>
    public async Task<bool> RevokeAsync(Guid deviceId, string actor = "operator", CancellationToken ct = default)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.Revoked, ct);
        if (device is null)
            return false;
        device.Revoked = true;
        db.Audits.Add(new Audit
        {
            TenantId = Tenant.DefaultId, Actor = actor, Action = "device.revoke",
            DeviceId = deviceId, At = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Apply a partial policy edit; returns the new version, or null if device unknown.</summary>
    public async Task<int?> UpdatePolicyAsync(Guid deviceId, PolicyPatch patch, string actor = "operator",
        CancellationToken ct = default)
    {
        var policy = await db.DevicePolicies.FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        if (policy is null)
            return null;

        // Clamp to the domain's valid range [1, MaxMinutes]: custom values arrive from the
        // Telegram bot, and the agent's ToScheduleRule() throws above MaxMinutes — a stored
        // out-of-range value would break policy application on the device.
        if (patch.PlayMinutes is { } play && play > 0)
            policy.PlayMinutes = Math.Min(play, ScheduleRule.MaxMinutes);
        if (patch.RestMinutes is { } rest && rest > 0)
            policy.RestMinutes = Math.Min(rest, ScheduleRule.MaxMinutes);
        if (patch.NightEnabled is { } ne) policy.NightEnabled = ne;
        if (patch.NightStart is { } ns) policy.NightStart = ns;
        if (patch.NightEnd is { } end) policy.NightEnd = end;
        if (patch.IntervalsEnabled is { } ie) policy.IntervalsEnabled = ie;
        if (patch.WordMonitorEnabled is { } wm) policy.WordMonitorEnabled = wm;
        if (patch.MonitorContextChars is { } mc && mc >= 0) policy.MonitorContextChars = Math.Min(mc, 200);
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

    /// <summary>
    /// Set the long-lived <c>paused</c> override (§6 desired-state). Bumps the desired version
    /// so it reaches the device on its next heartbeat and shows centrally as paused. Idempotent —
    /// setting the same value again is a no-op (no version churn).
    /// </summary>
    public async Task<int?> SetPausedAsync(Guid deviceId, bool paused, string actor = "operator",
        CancellationToken ct = default)
    {
        var desired = await db.DeviceDesired.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (desired is null)
            return null;

        if (desired.Paused == paused)
            return desired.Version; // no change → don't bump

        desired.Paused = paused;
        return await BumpAsync(desired, actor, paused ? "desired.pause" : "desired.resume", ct);
    }

    /// <summary>Set the <c>force_blocked</c> override (§6 desired-state). Idempotent.</summary>
    public async Task<int?> SetForceBlockedAsync(Guid deviceId, bool blocked, string actor = "operator",
        CancellationToken ct = default)
    {
        var desired = await db.DeviceDesired.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (desired is null)
            return null;
        if (desired.ForceBlocked == blocked)
            return desired.Version;

        desired.ForceBlocked = blocked;
        return await BumpAsync(desired, actor, blocked ? "desired.block" : "desired.unblock", ct);
    }

    /// <summary>Set the <c>night_bypass_until</c> override (§6 desired-state). Idempotent.</summary>
    public async Task<int?> SetNightBypassAsync(Guid deviceId, DateTimeOffset? until, string actor = "operator",
        CancellationToken ct = default)
    {
        var desired = await db.DeviceDesired.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
        if (desired is null)
            return null;
        if (desired.NightBypassUntil == until)
            return desired.Version;

        desired.NightBypassUntil = until;
        return await BumpAsync(desired, actor, "desired.night_bypass", ct);
    }

    private async Task<int> BumpAsync(DeviceDesired desired, string actor, string action, CancellationToken ct)
    {
        desired.Version += 1;
        desired.UpdatedAt = clock.GetUtcNow();

        db.Audits.Add(new Audit
        {
            TenantId = Tenant.DefaultId,
            Actor = actor,
            Action = action,
            DeviceId = desired.DeviceId,
            DetailJson = $"{{\"version\":{desired.Version}}}",
            At = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);
        return desired.Version;
    }
}
