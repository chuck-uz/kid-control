using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Handles agent heartbeats (§5.1): records the reported status + liveness, and answers with
/// a fresh policy/desired snapshot ONLY when the agent's held version is behind (delta sync,
/// §4). This is the backbone of policy propagation — an operator edit bumps the version, and
/// the next heartbeat from a stale agent carries the new policy down. Also raises the H2
/// night-attempt alert when a heartbeat carries a newer attempt time than we last announced.
/// </summary>
public sealed class HeartbeatService(
    FleetDbContext db,
    TimeProvider clock,
    NightAttemptTracker nightAttempts,
    DbAdminRegistry admins,
    ITelegramBotClient bot,
    ILogger<HeartbeatService> logger)
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

        // H2: a newer night-attempt time than we last announced → alert the operators once.
        if (nightAttempts.ShouldAlert(deviceId, req.Status.LastNightAttemptAt))
            await AlertNightAttemptAsync(device.Name, req.Status.LastNightAttemptAt!.Value, ct);

        return new HeartbeatResponse
        {
            // Send the snapshot only when the agent is behind — otherwise null (no-op heartbeat).
            Policy = policy is not null && policy.Version > req.PolicyVersion ? policy.ToDto() : null,
            Desired = desired is not null && desired.Version > req.DesiredVersion ? desired.ToDto() : null,
            HasCommands = hasCommands
        };
    }

    /// <summary>Tell every operator about a night-time usage attempt (time shown in Tashkent, UTC+5).</summary>
    private async Task AlertNightAttemptAsync(string deviceName, DateTimeOffset attemptAt, CancellationToken ct)
    {
        var local = attemptAt.ToOffset(TimeSpan.FromHours(5));
        var text = $"🌙 Попытка использования ПК ночью: «{deviceName}» в {local:HH:mm}.";
        foreach (var (chatId, _) in await admins.ListAsync(ct))
        {
            try { await bot.SendMessage(chatId, text, cancellationToken: ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Night-attempt alert to {ChatId} failed.", chatId); }
        }
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
