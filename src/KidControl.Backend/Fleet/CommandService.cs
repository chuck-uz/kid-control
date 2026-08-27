using System.Text.Json;
using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Fleet;

/// <summary>
/// One-shot commands (§6): enqueue (operator), long-poll delivery (agent), and ack. Delivery
/// is at-least-once (a command may be handed out again before its ack lands); the agent makes
/// APPLICATION at-most-once by id. TTL'd commands past their deadline are never delivered.
/// </summary>
public sealed class CommandService(FleetDbContext db, TimeProvider clock, CommandSignal signal)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Queue a command for a device with a TTL. Returns the new command id.</summary>
    public async Task<Guid?> EnqueueAsync(Guid deviceId, string type, IReadOnlyDictionary<string, string>? payload,
        TimeSpan ttl, string actor = "operator", CancellationToken ct = default)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == deviceId && !d.Revoked, ct))
            return null;

        var now = clock.GetUtcNow();
        var command = new Command
        {
            DeviceId = deviceId,
            Type = type,
            PayloadJson = payload is { Count: > 0 } ? JsonSerializer.Serialize(payload, FleetJson.Options) : null,
            CreatedAt = now,
            TtlAt = now + ttl
        };
        db.Commands.Add(command);
        db.Audits.Add(new Audit
        {
            TenantId = Tenant.DefaultId, Actor = actor, Action = $"command.{type}",
            DeviceId = deviceId, At = now
        });
        await db.SaveChangesAsync(ct);

        signal.Notify(deviceId);
        return command.Id;
    }

    /// <summary>
    /// Long-poll: return pending (unacked, unexpired) commands, waiting up to
    /// <paramref name="wait"/> for one to arrive. Marks delivered rows on the way out.
    /// </summary>
    public async Task<IReadOnlyList<CommandDto>> PollAsync(Guid deviceId, TimeSpan wait, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + wait;
        while (true)
        {
            var due = await LoadPendingAsync(deviceId, ct);
            if (due.Count > 0)
                return due;

            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return [];

            // Wake on enqueue, or fall through on a short interval to re-check (and re-evaluate TTL).
            var slice = remaining < PollInterval ? remaining : PollInterval;
            try { await signal.WaitAsync(deviceId, slice, ct); }
            catch (OperationCanceledException) { return []; }
        }
    }

    private async Task<IReadOnlyList<CommandDto>> LoadPendingAsync(Guid deviceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pending = await db.Commands
            .Where(c => c.DeviceId == deviceId && c.AckedAt == null && c.TtlAt > now)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return [];

        foreach (var c in pending)
            c.DeliveredAt ??= now;
        await db.SaveChangesAsync(ct);

        return pending.Select(ToDto).ToList();
    }

    /// <summary>Ack executed commands (idempotent, at-most-once): records result + acked time.</summary>
    public async Task AckAsync(Guid deviceId, CommandAckBatch batch, CancellationToken ct = default)
    {
        if (batch.Acks.Count == 0)
            return;

        var ids = batch.Acks.Select(a => Guid.TryParse(a.Id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        var rows = await db.Commands.Where(c => c.DeviceId == deviceId && ids.Contains(c.Id)).ToListAsync(ct);
        var now = clock.GetUtcNow();

        foreach (var row in rows)
        {
            if (row.AckedAt is not null)
                continue; // already acked — idempotent
            var ack = batch.Acks.First(a => a.Id == row.Id.ToString());
            row.AckedAt = now;
            row.Result = ack.Ok ? "ok" : Truncate(ack.Error ?? "error", 2000);
        }
        await db.SaveChangesAsync(ct);
    }

    private CommandDto ToDto(Command c) => new()
    {
        Id = c.Id.ToString(),
        Type = c.Type,
        Payload = c.PayloadJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(c.PayloadJson, FleetJson.Options),
        TtlAt = c.TtlAt
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
