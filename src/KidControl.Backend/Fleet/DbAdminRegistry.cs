using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Fleet;

/// <summary>
/// The operator whitelist backed by the <c>admin</c> table (decision #6). Replaces the agent's
/// file-based registry now that operators are central. Guards against removing the last admin
/// so the fleet can never lock everyone out.
/// </summary>
public sealed class DbAdminRegistry(FleetDbContext db)
{
    public Task<bool> IsAdminAsync(long chatId, CancellationToken ct = default)
        => db.Admins.AnyAsync(a => a.TenantId == Tenant.DefaultId && a.TelegramChatId == chatId, ct);

    public Task<int> CountAsync(CancellationToken ct = default)
        => db.Admins.CountAsync(a => a.TenantId == Tenant.DefaultId, ct);

    public async Task<IReadOnlyList<(long ChatId, string? Label)>> ListAsync(CancellationToken ct = default)
        => await db.Admins.AsNoTracking()
            .Where(a => a.TenantId == Tenant.DefaultId)
            .OrderBy(a => a.TelegramChatId)
            .Select(a => new ValueTuple<long, string?>(a.TelegramChatId, a.Label))
            .ToListAsync(ct);

    /// <summary>Add an admin. Returns false if that chat id is already an admin.</summary>
    public async Task<bool> AddAsync(long chatId, string? label = null, CancellationToken ct = default)
    {
        if (await IsAdminAsync(chatId, ct))
            return false;
        db.Admins.Add(new Admin { TenantId = Tenant.DefaultId, TelegramChatId = chatId, Label = label });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove an admin. Returns false if unknown or if it's the last admin.</summary>
    public async Task<bool> RemoveAsync(long chatId, CancellationToken ct = default)
    {
        var admin = await db.Admins.FirstOrDefaultAsync(
            a => a.TenantId == Tenant.DefaultId && a.TelegramChatId == chatId, ct);
        if (admin is null)
            return false;
        if (await CountAsync(ct) <= 1)
            return false; // never remove the last admin

        db.Admins.Remove(admin);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
