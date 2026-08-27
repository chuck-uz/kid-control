using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Handles agent heartbeats (§5.1): records the reported status + liveness, and answers with
/// a fresh policy/desired snapshot ONLY when the agent's held version is behind (delta sync,
/// §4). This is the backbone of policy propagation — an operator edit bumps the version, and
/// the next heartbeat from a stale agent carries the new policy down.
/// </summary>
public sealed class HeartbeatService(FleetDbContext db, TimeProvider clock)
{
    public async Task<HeartbeatResponse?> HandleAsync(Guid deviceId, HeartbeatRequest req, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.Revoked, ct);
        if (device is null)
            return null; // revoked between auth and here, or unknown

        device.LastSeenAt = now;
        if (!string.IsNullOrWhiteSpace(req.Status.AgentVersion))
            device.AgentVersion = req.Status.AgentVersion;

        await UpsertStatusAsync(deviceId, req.Status, now, ct);

        var policy = await db.DevicePolicies.AsNoTracking().FirstOrDefaultAsync(p => p.DeviceId == deviceId, ct);
        var desired = await db.DeviceDesired.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);

        var hasCommands = await db.Commands
            .AnyAsync(c => c.DeviceId == deviceId && c.AckedAt == null && c.TtlAt > now, ct);

        await db.SaveChangesAsync(ct);

        return new HeartbeatResponse
        {
            // Send the snapshot only when the agent is behind — otherwise null (no-op heartbeat).
            Policy = policy is not null && policy.Version > req.PolicyVersion ? policy.ToDto() : null,
            Desired = desired is not null && desired.Version > req.DesiredVersion ? desired.ToDto() : null,
            HasCommands = hasCommands
        };
    }

    private async Task UpsertStatusAsync(Guid deviceId, StatusReportDto status, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.DeviceStatuses.FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (row is null)
        {
            row = new DeviceStatus { DeviceId = deviceId };
            db.DeviceStatuses.Add(row);
        }

        row.Status = status.Status;
        row.TimeRemaining = status.TimeRemaining;
        row.IsNight = status.IsNight;
        row.IsUnlimited = status.IsUnlimited;
        row.ShutdownInSeconds = status.ShutdownInSeconds;
        row.ReportedAt = now;
    }
}
