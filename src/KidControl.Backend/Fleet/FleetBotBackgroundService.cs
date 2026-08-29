using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TgUpdate = Telegram.Bot.Types.Update;

namespace KidControl.Backend.Fleet;

/// <summary>
/// The fleet operator bot, hosted inside the backend. Long-polls Telegram. You pick a device
/// ONCE (🔀 Устройства → the inline list); it stays selected per chat, and the persistent bottom
/// keyboard (📊/➕/🎮/💻/⚙️/🌙/📦/…) then acts on that device until you switch. Folder buttons open
/// small inline sub-menus whose actions carry the device id, so they survive a bot restart. All
/// side effects go through the fleet services via <see cref="FleetBotActions"/>.
/// </summary>
public sealed class FleetBotBackgroundService(
    ITelegramBotClient bot,
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ScreenshotRelay screenshots,
    ILogger<FleetBotBackgroundService> logger) : BackgroundService
{
    private bool Enabled => !string.IsNullOrWhiteSpace(BotToken);
    private string? BotToken => config["Telegram:BotToken"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

    // Per-chat state: the currently selected device, and (transiently) a device awaiting a rename.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Guid> _selected = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Guid> _awaitingRename = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Guid> _awaitingTarget = new();

    // ── Bottom (persistent) keyboard — the control folders ────────────────────
    private const string BtnStatus = "📊 Статус", BtnTime = "➕ Время", BtnApp = "🎮 Приложение",
        BtnPc = "💻 Компьютер", BtnRules = "⚙️ Интервалы", BtnNight = "🌙 Ночь", BtnVer = "📦 Версия",
        BtnHistory = "🧾 История", BtnName = "✏️ Имя", BtnRevoke = "🗑️ Отозвать",
        BtnSwitch = "🔀 Устройства", BtnAdmins = "👤 Админы";

    private static readonly ReplyKeyboardMarkup MainKeyboard = new(new[]
    {
        new KeyboardButton[] { BtnStatus, BtnTime },
        new KeyboardButton[] { BtnApp, BtnPc },
        new KeyboardButton[] { BtnRules, BtnNight },
        new KeyboardButton[] { BtnVer, BtnHistory },
        new KeyboardButton[] { BtnName, BtnRevoke },
        new KeyboardButton[] { BtnSwitch, BtnAdmins },
    })
    { ResizeKeyboard = true };

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

        // Awaiting a new device name? The next non-command message is the name.
        if (_awaitingRename.TryRemove(chatId, out var renameId))
        {
            if (!t.StartsWith('/') && !IsKeyboardButton(t))
            {
                await Send(chatId, await actions.RenameAsync(renameId, t, ct), ct);
                return;
            }
            await Send(chatId, "Переименование отменено.", ct);
        }

        // Awaiting a target version tag? The next non-command message is the tag (or "latest").
        if (_awaitingTarget.TryRemove(chatId, out var targetId))
        {
            if (!t.StartsWith('/') && !IsKeyboardButton(t))
            {
                await Send(chatId, await actions.SetTargetVersionAsync(targetId, t, ct), ct);
                return;
            }
            await Send(chatId, "Выбор версии отменён.", ct);
        }

        // ── Global commands (not device-scoped) ──
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
        if (t is "/admins" or BtnAdmins) { await Send(chatId, await actions.AdminsTextAsync(ct), ct); return; }
        if (t is "/enroll") { await SendHtml(chatId, await actions.NewEnrollCodeAsync(ct), ct); return; }
        if (t is "/all") { await Send(chatId, await actions.OverviewTextAsync(ct), ct); return; }
        if (t is "/start" or "/menu" or BtnSwitch) { await ShowDeviceListAsync(actions, chatId, ct); return; }

        // ── Device-scoped folders (operate on the selected device) ──
        if (IsKeyboardButton(t))
        {
            var resolved = await ResolveDeviceAsync(actions, chatId, ct);
            if (resolved is not { } id)
            {
                // 0 devices, or several with no clear pick → show the list (offline flagged there).
                await ShowDeviceListAsync(actions, chatId, ct);
                return;
            }

            switch (t)
            {
                case BtnStatus: await OpenFolderAsync(actions, chatId, id, "status", ct); return;
                case BtnTime: await OpenFolderAsync(actions, chatId, id, "time", ct); return;
                case BtnApp: await OpenFolderAsync(actions, chatId, id, "app", ct); return;
                case BtnPc: await OpenFolderAsync(actions, chatId, id, "pc", ct); return;
                case BtnRules: await OpenFolderAsync(actions, chatId, id, "rules", ct); return;
                case BtnNight: await OpenFolderAsync(actions, chatId, id, "night", ct); return;
                case BtnVer: await OpenFolderAsync(actions, chatId, id, "ver", ct); return;
                case BtnHistory: await Send(chatId, await actions.HistoryTextAsync(id, ct), ct); return;
                case BtnName: _awaitingRename[chatId] = id; await Send(chatId, "✏️ Отправьте новое имя устройства одним сообщением:", ct); return;
                case BtnRevoke: await ShowConfirmAsync(chatId, id, "Отозвать устройство?", $"rvok:{id}", ct); return;
            }
        }

        // Anything else → the device list.
        await ShowDeviceListAsync(actions, chatId, ct);
    }

    private static bool IsKeyboardButton(string t) => t is BtnStatus or BtnTime or BtnApp or BtnPc
        or BtnRules or BtnNight or BtnVer or BtnHistory or BtnName or BtnRevoke or BtnSwitch or BtnAdmins;

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

        var offline = list.Where(d => !IsOnline(d)).Select(d => d.Name).ToList();
        var header = offline.Count == 0
            ? "Выберите устройство:"
            : $"⚠️ Не на связи: {string.Join(", ", offline)}.\nВыберите устройство:";

        await bot.SendMessage(chatId, header, replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: ct);
    }

    private static bool IsOnline(DeviceSummary d)
        => d.LastSeenAt is { } ls && (DateTimeOffset.UtcNow - ls) < TimeSpan.FromMinutes(3);

    private static string DeviceLabel(DeviceSummary d)
    {
        if (IsOnline(d))
            return $"🟢 {d.Name} — {d.Status ?? "?"}";
        var ago = d.LastSeenAt is { } ls ? $"{(DateTimeOffset.UtcNow - ls).TotalMinutes:F0} мин" : "никогда";
        return $"⚪ {d.Name} — оффлайн ({ago})";
    }

    /// <summary>
    /// The device this chat should act on, WITHOUT prompting when it's unambiguous: keep the
    /// sticky selection if it still exists; else auto-pick when there is exactly one device.
    /// Returns null only when there are zero, or several with no valid selection — then the
    /// caller shows the picker (which flags offline devices).
    /// </summary>
    private async Task<Guid?> ResolveDeviceAsync(FleetBotActions actions, long chatId, CancellationToken ct)
    {
        var list = await actions.ListDevicesAsync(ct);
        if (_selected.TryGetValue(chatId, out var id) && list.Any(d => d.Id == id))
            return id;
        _selected.TryRemove(chatId, out _);
        if (list.Count == 1)
        {
            _selected[chatId] = list[0].Id;
            return list[0].Id;
        }
        return null;
    }

    /// <summary>Select a device for this chat and show its status with the persistent control keyboard.</summary>
    private async Task SelectDeviceAsync(FleetBotActions actions, long chatId, Guid id, CancellationToken ct)
    {
        _selected[chatId] = id;
        var status = await actions.StatusTextAsync(id, ct);
        await bot.SendMessage(chatId, "✅ Выбрано устройство.\n\n" + status, replyMarkup: MainKeyboard, cancellationToken: ct);
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

        if (kind == "nav") { await SelectDeviceAsync(actions, chatId, deviceId, ct); return; }
        if (kind == "f") { await OpenFolderAsync(actions, chatId, deviceId, tail.FirstOrDefault() ?? "", ct); return; }
        if (kind == "rvok") { await Send(chatId, await actions.RevokeAsync(deviceId, ct), ct); return; }
        if (kind == "vtag") { _awaitingTarget[chatId] = deviceId; await Send(chatId, "✏️ Отправьте тег версии одним сообщением (например 2.2.0) или latest:", ct); return; }
        if (kind == "shot")
        {
            var uploadId = Guid.NewGuid().ToString("N");
            screenshots.Register(uploadId, chatId, deviceId);
            await Send(chatId, await actions.RequestScreenshotAsync(deviceId, uploadId, ct), ct);
            return;
        }
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
            "settarget" => await a.SetTargetVersionAsync(id, tail.ElementAtOrDefault(1) ?? "latest", ct),
            "updatenow" => await a.UpdateNowAsync(id, null, ct),
            _ => "Неизвестное действие."
        };
    }

    // ── Folder sub-menus (inline, scoped to the device) ───────────────────────
    private async Task OpenFolderAsync(FleetBotActions actions, long chatId, Guid id, string folder, CancellationToken ct)
    {
        InlineKeyboardButton B(string label, string action) => InlineKeyboardButton.WithCallbackData(label, action);

        InlineKeyboardMarkup kb;
        string title;
        switch (folder)
        {
            case "status":
                title = await actions.StatusTextAsync(id, ct);
                kb = new(new[] { new[] { B("🚫 Блок", $"a:{id}:block"), B("✅ Разблок", $"a:{id}:unblock") },
                                 new[] { B("🔄 Сбросить таймер", $"a:{id}:reset") } });
                break;
            case "time":
                title = "Добавить время:";
                kb = new(new[] { new[] { B("+15", $"a:{id}:addtime:15"), B("+30", $"a:{id}:addtime:30") },
                                 new[] { B("+60", $"a:{id}:addtime:60"), B("+120", $"a:{id}:addtime:120") } });
                break;
            case "app":
                title = "Контроль:";
                kb = new(new[] { new[] { B("⏸️ Пауза", $"a:{id}:pause"), B("▶️ Продолжить", $"a:{id}:resume") } });
                break;
            case "pc":
                title = "Компьютер:";
                kb = new(new[] { new[] { B("📷 Скриншот", $"shot:{id}") },
                                 new[] { B("🔌 Выключить", $"a:{id}:shutdown"), B("🔄 Перезагрузить", $"a:{id}:restart") } });
                break;
            case "rules":
                title = "Режим (игра/отдых):";
                kb = new(new[] { new[] { B("60/15", $"a:{id}:setrule:60:15"), B("45/15", $"a:{id}:setrule:45:15") },
                                 new[] { B("40/20", $"a:{id}:setrule:40:20"), B("30/10", $"a:{id}:setrule:30:10") },
                                 new[] { B("♾️ Откл интервалы", $"a:{id}:intervals:off"), B("✅ Вкл", $"a:{id}:intervals:on") } });
                break;
            case "night":
                title = "Ночной режим:";
                kb = new(new[] { new[] { B("🌙 Вкл", $"a:{id}:night:on"), B("🔕 Выкл", $"a:{id}:night:off") },
                                 new[] { B("22:00-07:00", $"a:{id}:nightwin:2200:0700"), B("23:00-06:00", $"a:{id}:nightwin:2300:0600") },
                                 new[] { B("🌙 Снять ночь на сегодня", $"a:{id}:bypass") } });
                break;
            case "ver":
                title = await actions.VersionTextAsync(id, ct);
                var verRows = new List<InlineKeyboardButton[]>
                {
                    new[] { B("🆕 Следить за latest", $"a:{id}:settarget:latest") },
                };
                // Pin to exactly the version the device runs now, if we know it (stops it
                // auto-moving past this build).
                var current = (await actions.ListDevicesAsync(ct)).FirstOrDefault(x => x.Id == id)?.AgentVersion;
                if (!string.IsNullOrWhiteSpace(current))
                    verRows.Add(new[] { B($"📌 Закрепить {current}", $"a:{id}:settarget:{current}") });
                verRows.Add(new[] { B("✏️ Задать версию", $"vtag:{id}") });
                verRows.Add(new[] { B("⬇️ Обновить сейчас", $"a:{id}:updatenow") });
                kb = new(verRows.ToArray());
                break;
            default:
                await SelectDeviceAsync(actions, chatId, id, ct);
                return;
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
