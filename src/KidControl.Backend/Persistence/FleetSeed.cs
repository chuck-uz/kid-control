using KidControl.Backend.Entities;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Persistence;

/// <summary>
/// Applies pending migrations on boot and idempotently seeds the operator admin. The tenant
/// row is seeded by the migration itself; the admin lives here because its Telegram chat id
/// is deployment configuration (env <c>FLEET_ADMIN_CHAT_ID</c> / key <c>Seed:AdminChatId</c>),
/// not a value we'd ever commit. Safe to run on every startup.
/// </summary>
public static class FleetSeed
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<FleetDbContext>();
        var config = sp.GetRequiredService<IConfiguration>();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("FleetSeed");

        await db.Database.MigrateAsync(ct);

        // Belt-and-braces: the migration seeds the tenant, but guarantee it before FK use.
        if (!await db.Tenants.AnyAsync(t => t.Id == Tenant.DefaultId, ct))
        {
            db.Tenants.Add(new Tenant { Id = Tenant.DefaultId, Name = "Семья" });
            await db.SaveChangesAsync(ct);
        }

        var chatId = config.GetValue<long?>("Seed:AdminChatId")
            ?? ParseLong(Environment.GetEnvironmentVariable("FLEET_ADMIN_CHAT_ID"));

        if (chatId is null or 0)
        {
            log.LogWarning(
                "No admin seeded: set FLEET_ADMIN_CHAT_ID (or Seed:AdminChatId) to the operator's Telegram chat id.");
            return;
        }

        var exists = await db.Admins.AnyAsync(
            a => a.TenantId == Tenant.DefaultId && a.TelegramChatId == chatId.Value, ct);
        if (exists)
            return;

        db.Admins.Add(new Admin
        {
            TenantId = Tenant.DefaultId,
            TelegramChatId = chatId.Value,
            Label = config["Seed:AdminLabel"]
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded operator admin with chat id {ChatId}.", chatId.Value);
    }

    private static long? ParseLong(string? s) => long.TryParse(s, out var v) ? v : null;
}
