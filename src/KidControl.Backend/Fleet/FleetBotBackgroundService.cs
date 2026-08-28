using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TgUpdate = Telegram.Bot.Types.Update;

namespace KidControl.Backend.Fleet;

/// <summary>
/// The fleet operator bot, hosted inside the backend (T11). Long-polls Telegram; the top level
/// is the device list, selecting a device opens the familiar folder menu (📊/➕/🎮/💻/⚙️/🌙/📦/👤)
/// bound to that device, and every action runs through the fleet services (policy/desired edits
/// and the command queue) via <see cref="FleetBotActions"/>. Media buttons are labelled Phase 2.
/// Non-admin chats are ignored; a per-chat selection is remembered but the device id is also
/// carried in every callback so a bot restart doesn't strand a menu.
/// </summary>
public sealed class FleetBotBackgroundService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<FleetBotBackgroundService> logger) : BackgroundService
{
    private bool Enabled => !string.IsNullOrWhiteSpace(BotToken);
    private string? BotToken => config["Telegram:BotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            logger.LogInformation("Fleet bot disabled: no Telegram:BotToken configured.");
            return;
        }

        var offset = await DropPendingAsync(stoppingToken);
        logger.LogInformation("Fleet bot started (offset {Offset}).", offset);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(offset, timeout: 20, cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    await HandleSafeAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fleet bot poll failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<int> DropPendingAsync(CancellationToken ct)
    {
        try
        {
            var latest = await bot.GetUpdates(offset: -1, timeout: 0, cancellationToken: ct);
            return latest.Length > 0 ? latest[^1].Id + 1 : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to drop pending updates; starting from 0.");
            return 0;
        }
    }

    private async Task HandleSafeAsync(TgUpdate update, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var actions = scope.ServiceProvider.GetRequiredService<FleetBotActions>();

            if (update is { Message: { Text: { } text } message })
                await HandleMessageAsync(actions, message.Chat.Id, text, ct);
            else if (update is { CallbackQuery: { } cb })
                await HandleCallbackAsync(actions, cb, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fleet bot failed to handle update {UpdateId}.", update.Id);
        }
    }

    // ── Messages ─────────────────────────────────────────────────────────────
    private async Task HandleMessageAsync(FleetBotActions actions, long chatId, string text, CancellationToken ct)
    {
        var t = text.Trim();

        if (t.StartsWith("/myid", StringComparison.OrdinalIgnoreCase))
        {
            await Send(chatId, $"Ваш ID: {chatId}", ct);
            return;
        }

        if (!await actions.IsAdminAsync(chatId, ct))
        {
            await Send(chatId, $"⛔ Нет доступа. Ваш ID: {chatId}\nПопросите администратора: /addadmin {chatId}", ct);
            return;
        }

        if (t.StartsWith("/addadmin", StringComparison.OrdinalIgnoreCase))
        {
            await Send(chatId, await AdminChangeAsync(actions, t, add: true, ct), ct);
            return;
        }
        if (t.StartsWith("/deladmin", StringComparison.OrdinalIgnoreCase))
        {
            await Send(chatId, await AdminChangeAsync(actions, t, add: false, ct), ct);
            return;
        }
        if (t is "/admins" or "👤 Админы")
        {
            await Send(chatId, await actions.AdminsTextAsync(ct), ct);
            return;
        }
        if (t is "/enroll")
        {
            await SendHtml(chatId, await actions.NewEnrollCodeAsync(ct), ct);
            return;
        }
        if (t is "/all")
        {
            await Send(chatId, await actions.OverviewTextAsync(ct), ct);
            return;
        }

        // Default: show the device list (the top level).
        await ShowDeviceListAsync(actions, chatId, ct);
    }

    private async Task ShowDeviceListAsync(FleetBotActions actions, long chatId, CancellationToken ct)
    {
        var list = await actions.ListDevicesAsync(ct);
        if (list.Count == 0)
        {
            await Send(chatId, "Устройств пока нет. Создайте код привязки: /enroll", ct);
            return;
        }

        var rows = list.Select(d => new[]
        {
            InlineKeyboardButton.WithCallbackData(DeviceLabel(d), $"nav:{d.Id}")
        }).ToList();
        rows.Add([InlineKeyboardButton.WithCallbackData("📋 Все устройства", "all"),
                  InlineKeyboardButton.WithCallbackData("🔑 /enroll", "enroll")]);

        await bot.SendMessage(chatId, "Выберите устройство:", replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: ct);
    }

    private static string DeviceLabel(DeviceSummary d)
    {
        var online = d.LastSeenAt is { } ls && (DateTimeOffset.UtcNow - ls) < TimeSpan.FromMinutes(3) ? "🟢" : "⚪";
        return $"{online} {d.Name} — {d.Status ?? "?"}";
    }

    // ── Callbacks ────────────────────────────────────────────────────────────
    private async Task HandleCallbackAsync(FleetBotActions actions, CallbackQuery cb, CancellationToken ct)
    {
        var chatId = cb.Message?.Chat.Id ?? cb.From.Id;
        if (!await actions.IsAdminAsync(chatId, ct))
        {
            await bot.AnswerCallbackQuery(cb.Id, "Недостаточно прав.", showAlert: true, cancellationToken: ct);
            return;
        }

        var data = cb.Data ?? "";
        await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);

        // Non device-scoped.
        switch (data)
        {
            case "home": await ShowDeviceListAsync(actions, chatId, ct); return;
            case "all": await Send(chatId, await actions.OverviewTextAsync(ct), ct); return;
            case "enroll": await SendHtml(chatId, await actions.NewEnrollCodeAsync(ct), ct); return;
            case "x": return;
        }

        var parts = data.Split(':');
        if (parts.Length < 2 || !Guid.TryParse(parts[1], out var deviceId))
            return;
        var kind = parts[0];
        var tail = parts.Skip(2).ToArray();

        if (kind == "nav") { await ShowDeviceMenuAsync(chatId, deviceId, await actions.StatusTextAsync(deviceId, ct), ct); return; }
        if (kind == "f") { await ShowFolderAsync(chatId, deviceId, tail.FirstOrDefault() ?? "", ct); return; }
        if (kind == "rv") { await ShowConfirmAsync(chatId, deviceId, "Отозвать устройство?", $"rvok:{deviceId}", ct); return; }
        if (kind == "rvok") { await Send(chatId, await actions.RevokeAsync(deviceId, ct), ct); return; }
        if (kind == "a") { await Send(chatId, await RunActionAsync(actions, deviceId, tail, ct), ct); return; }
    }

    private async Task<string> RunActionAsync(FleetBotActions a, Guid id, string[] tail, CancellationToken ct)
    {
        var verb = tail.FirstOrDefault() ?? "";
        return verb switch
        {
            "addtime" => await a.AddTimeAsync(id, ParseInt(tail, 1, 30), ct),
            "reset" => await a.ResetTimerAsync(id, ct),
            "pause" => await a.PauseAsync(id, true, ct),
            "resume" => await a.PauseAsync(id, false, ct),
            "block" => await a.BlockAsync(id, true, ct),
            "unblock" => await a.BlockAsync(id, false, ct),
            "shutdown" => await a.ShutdownAsync(id, ct),
            "restart" => await a.RestartAsync(id, ct),
            "setrule" => await a.SetRuleAsync(id, ParseInt(tail, 1, 40), ParseInt(tail, 2, 20), ct),
            "intervals" => await a.SetIntervalsAsync(id, tail.ElementAtOrDefault(1) == "on", ct),
            "night" => await a.SetNightEnabledAsync(id, tail.ElementAtOrDefault(1) == "on", ct),
            "nightwin" => await a.SetNightWindowAsync(id, ParseHm(tail, 1), ParseHm(tail, 2), ct),
            "bypass" => await a.NightBypassAsync(id, DateTimeOffset.UtcNow.AddHours(10), ct),
            "updatenow" => await a.UpdateNowAsync(id, null, ct),
            "media" => "📷 Медиа-команды (скриншот/аудио) — Phase 2, появятся позже.",
            _ => "Неизвестное действие."
        };
    }

    // ── Keyboards ────────────────────────────────────────────────────────────
    private async Task ShowDeviceMenuAsync(long chatId, Guid id, string statusText, CancellationToken ct)
    {
        InlineKeyboardButton B(string label, string action) => InlineKeyboardButton.WithCallbackData(label, action);
        var kb = new InlineKeyboardMarkup(new[]
        {
            new[] { B("📊 Статус", $"f:{id}:status"), B("➕ Время", $"f:{id}:time") },
            new[] { B("🎮 Приложение", $"f:{id}:app"), B("💻 Компьютер", $"f:{id}:pc") },
            new[] { B("⚙️ Интервалы", $"f:{id}:rules"), B("🌙 Ночь", $"f:{id}:night") },
            new[] { B("📦 Версия", $"f:{id}:ver"), B("👤 Админы", "adm") },
            new[] { B("🗑️ Отозвать", $"rv:{id}"), B("⬅️ Устройства", "home") }
        });
        await bot.SendMessage(chatId, statusText, replyMarkup: kb, cancellationToken: ct);
    }

    private async Task ShowFolderAsync(long chatId, Guid id, string folder, CancellationToken ct)
    {
        InlineKeyboardButton B(string label, string action) => InlineKeyboardButton.WithCallbackData(label, action);
        var back = new[] { B("⬅️ Меню", $"nav:{id}") };

        InlineKeyboardMarkup kb;
        string title;
        switch (folder)
        {
            case "status":
                title = "Управление статусом:";
                kb = new(new[] { new[] { B("🚫 Блок", $"a:{id}:block"), B("✅ Разблок", $"a:{id}:unblock") },
                                 new[] { B("🔄 Сбросить таймер", $"a:{id}:reset") }, back });
                break;
            case "time":
                title = "Добавить время:";
                kb = new(new[] { new[] { B("+15", $"a:{id}:addtime:15"), B("+30", $"a:{id}:addtime:30") },
                                 new[] { B("+60", $"a:{id}:addtime:60"), B("+120", $"a:{id}:addtime:120") }, back });
                break;
            case "app":
                title = "Контроль:";
                kb = new(new[] { new[] { B("⏸️ Пауза", $"a:{id}:pause"), B("▶️ Продолжить", $"a:{id}:resume") }, back });
                break;
            case "pc":
                title = "Компьютер:";
                kb = new(new[] { new[] { B("📷 Скриншот (Phase 2)", $"a:{id}:media") },
                                 new[] { B("🔌 Выключить", $"a:{id}:shutdown"), B("🔄 Перезагрузить", $"a:{id}:restart") }, back });
                break;
            case "rules":
                title = "Режим (игра/отдых):";
                kb = new(new[] { new[] { B("60/15", $"a:{id}:setrule:60:15"), B("45/15", $"a:{id}:setrule:45:15") },
                                 new[] { B("40/20", $"a:{id}:setrule:40:20"), B("30/10", $"a:{id}:setrule:30:10") },
                                 new[] { B("♾️ Откл", $"a:{id}:intervals:off"), B("✅ Вкл", $"a:{id}:intervals:on") }, back });
                break;
            case "night":
                title = "Ночной режим:";
                kb = new(new[] { new[] { B("🌙 Вкл", $"a:{id}:night:on"), B("🔕 Выкл", $"a:{id}:night:off") },
                                 new[] { B("22:00-07:00", $"a:{id}:nightwin:2200:0700"), B("23:00-06:00", $"a:{id}:nightwin:2300:0600") },
                                 new[] { B("🌙 Снять ночь на сегодня", $"a:{id}:bypass") }, back });
                break;
            case "ver":
                title = "Версия / обновление:";
                kb = new(new[] { new[] { B("⬇️ Обновить сейчас", $"a:{id}:updatenow") }, back });
                break;
            default:
                title = "Меню:";
                kb = new(new[] { back });
                break;
        }
        await bot.SendMessage(chatId, title, replyMarkup: kb, cancellationToken: ct);
    }

    private async Task ShowConfirmAsync(long chatId, Guid id, string text, string yesData, CancellationToken ct)
    {
        var kb = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("⚠️ Да", yesData),
                    InlineKeyboardButton.WithCallbackData("Отмена", "x") }
        });
        await bot.SendMessage(chatId, text, replyMarkup: kb, cancellationToken: ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private async Task<string> AdminChangeAsync(FleetBotActions actions, string text, bool add, CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var target))
            return add ? "Формат: /addadmin <ID>" : "Формат: /deladmin <ID>";
        return add ? await actions.AddAdminAsync(target, ct) : await actions.RemoveAdminAsync(target, ct);
    }

    private static int ParseInt(string[] tail, int idx, int fallback)
        => tail.ElementAtOrDefault(idx) is { } s && int.TryParse(s, out var v) ? v : fallback;

    private static TimeSpan ParseHm(string[] tail, int idx)
    {
        var s = tail.ElementAtOrDefault(idx) ?? "0000";
        return s.Length == 4 && int.TryParse(s[..2], out var h) && int.TryParse(s[2..], out var m)
            ? new TimeSpan(h, m, 0) : TimeSpan.Zero;
    }

    private Task Send(long chatId, string text, CancellationToken ct)
        => bot.SendMessage(chatId, text, cancellationToken: ct);

    private Task SendHtml(long chatId, string text, CancellationToken ct)
        => bot.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: ct);
}
