using System.Collections.Concurrent;
using KidControl.Backend.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Liveness alerts (RFC-02 F1). Periodically checks each device's <c>last_seen</c>; when a
/// device crosses into "offline" (no heartbeat for longer than the threshold) it notifies the
/// operators once, and once again when it comes back. Anti-spam is by state transition — a
/// device that stays offline is not re-announced every cycle. Backend-only: needs no agent change.
/// </summary>
public sealed class AlertBackgroundService(
    IServiceScopeFactory scopeFactory,
    ITelegramBotClient bot,
    IConfiguration config,
    TimeProvider clock,
    ILogger<AlertBackgroundService> logger) : BackgroundService
{
    // deviceId -> true when we've told operators it's offline (so we don't repeat).
    private readonly ConcurrentDictionary<Guid, bool> _offline = new();

    private bool Enabled => !string.IsNullOrWhiteSpace(
        config["Telegram:BotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));

    private TimeSpan OfflineAfter => TimeSpan.FromSeconds(config.GetValue("Alerts:OfflineAfterSeconds", 180));
    private TimeSpan CheckEvery => TimeSpan.FromSeconds(config.GetValue("Alerts:CheckIntervalSeconds", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            logger.LogInformation("Alert service disabled: no Telegram bot token.");
            return;
        }

        // Prime state on the first pass so a backend restart doesn't re-announce steady state.
        var primed = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(announce: primed, stoppingToken);
                primed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Alert check failed.");
            }

            try { await Task.Delay(CheckEvery, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync(bool announce, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
        var admins = scope.ServiceProvider.GetRequiredService<DbAdminRegistry>();

        var now = clock.GetUtcNow();
        var devices = await db.Devices.AsNoTracking()
            .Where(d => !d.Revoked)
            .Select(d => new { d.Id, d.Name, d.LastSeenAt })
            .ToListAsync(ct);

        foreach (var d in devices)
        {
            var isOffline = IsOffline(d.LastSeenAt, now, OfflineAfter);
            var wasOffline = _offline.TryGetValue(d.Id, out var flag) && flag;

            if (isOffline == wasOffline)
                continue;

            _offline[d.Id] = isOffline;
            if (!announce)
                continue; // first pass: just record the baseline, don't spam on restart

            var text = isOffline
                ? $"⚠️ Устройство «{d.Name}» не на связи (нет heartbeat > {OfflineAfter.TotalMinutes:F0} мин)."
                : $"🟢 Устройство «{d.Name}» снова на связи.";
            await NotifyAdminsAsync(admins, text, ct);
        }
    }

    /// <summary>A device is offline if it has never reported, or its last heartbeat is older than the threshold.</summary>
    public static bool IsOffline(DateTimeOffset? lastSeen, DateTimeOffset now, TimeSpan threshold)
        => lastSeen is null || (now - lastSeen.Value) > threshold;

    private async Task NotifyAdminsAsync(DbAdminRegistry admins, string text, CancellationToken ct)
    {
        foreach (var (chatId, _) in await admins.ListAsync(ct))
        {
            try { await bot.SendMessage(chatId, text, cancellationToken: ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Alert to {ChatId} failed.", chatId); }
        }
    }
}
