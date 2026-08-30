using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Handles a content-monitor hit pushed by the agent (RFC-05): applies anti-spam
/// (<see cref="WordAlertTracker"/>), records metadata-only (<see cref="WordAlert"/> — no context,
/// no screenshot), and pushes the alert with the screenshot to every operator's Telegram chat.
/// The context snippet and the image live only in Telegram, never in the DB.
/// </summary>
public class WordAlertService(
    FleetDbContext db,
    TimeProvider clock,
    WordAlertTracker tracker,
    DbAdminRegistry admins,
    ITelegramBotClient bot,
    ILogger<WordAlertService> logger)
{
    public const int MaxScreenshotBytes = 8 * 1024 * 1024;

    public async Task<bool> HandleAsync(Guid deviceId, WordAlertDto dto, byte[]? screenshot, CancellationToken ct = default)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && !d.Revoked, ct);
        if (device is null)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var decision = tracker.Decide(deviceId, dto.Category, dto.Term, dto.Source, now);
        if (decision == WordAlertTracker.Decision.Suppress)
        {
            return true; // accepted, but deduped/over the ceiling
        }

        // Metadata only (no context, no image).
        db.WordAlerts.Add(new WordAlert
        {
            DeviceId = deviceId, Category = dto.Category, Term = dto.Term, Source = dto.Source, At = now
        });
        await db.SaveChangesAsync(ct);

        if (decision == WordAlertTracker.Decision.Rollup)
        {
            await BroadcastAsync(device.Name,
                $"🔕 «{device.Name}»: слишком много совпадений — дальнейшие за эту минуту свёрнуты.",
                null, ct);
            return true;
        }

        var shot = screenshot is { Length: > 0 and <= MaxScreenshotBytes } ? screenshot : null;
        await BroadcastAsync(device.Name, BuildCaption(device.Name, dto, now), shot, ct);
        return true;
    }

    private static string BuildCaption(string deviceName, WordAlertDto dto, DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(5)); // Tashkent
        var head = dto.Category == "adult" ? "🔞 Взрослый контент" : "🤬 Плохое слово";
        var src = dto.Source switch
        {
            "keyboard" => "клавиатура",
            "window" => "окно",
            "url" => "URL",
            _ => dto.Source
        };
        var ctx = string.IsNullOrWhiteSpace(dto.Context) ? "" : $"\nКонтекст: {dto.Context}";
        return $"{head}\nУстройство: {deviceName}\nСовпадение: «{dto.Term}»\nИсточник: {src}{ctx}\nВремя: {local:HH:mm}";
    }

    /// <summary>Sends to every operator; with a screenshot when provided, else a text message.</summary>
    protected virtual async Task BroadcastAsync(string deviceName, string caption, byte[]? screenshot, CancellationToken ct)
    {
        foreach (var (chatId, _) in await admins.ListAsync(ct))
        {
            try
            {
                if (screenshot is not null)
                {
                    await bot.SendPhoto(chatId, InputFile.FromStream(new MemoryStream(screenshot), "alert.jpg"),
                        caption: caption, cancellationToken: ct);
                }
                else
                {
                    await bot.SendMessage(chatId, caption, cancellationToken: ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Word-alert to {ChatId} failed.", chatId);
            }
        }
    }
}
