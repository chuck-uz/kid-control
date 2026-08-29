using KidControl.Fleet.Contracts;

namespace KidControl.Backend.Fleet;

/// <summary>
/// The bot's device-scoped operations, expressed against the fleet services and returning
/// ready-to-send Russian replies. Kept free of Telegram types so the whole action layer is
/// unit-testable against an in-memory database. State edits go through policy/desired (version
/// bumps → reconciled on heartbeat); imperative actions go through the command queue.
/// </summary>
public sealed class FleetBotActions(
    DeviceAdminService devices,
    CommandService commands,
    EnrollmentService enrollment,
    DbAdminRegistry admins)
{
    private static readonly TimeSpan CommandTtl = TimeSpan.FromMinutes(5);

    // Show times in Tashkent (UTC+5, no DST). Resolved from tzdata if present, else a fixed +5
    // custom zone — so it's correct even in a minimal container without the tz database.
    private static readonly TimeZoneInfo Tz = ResolveTashkent();

    private static TimeZoneInfo ResolveTashkent()
    {
        foreach (var id in new[] { "Asia/Tashkent", "Uzbekistan Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try next */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("UZT", TimeSpan.FromHours(5), "UZT (UTC+5)", "UZT");
    }

    private static DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Tz);

    // ── Devices ──────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(CancellationToken ct = default)
        => devices.ListDevicesAsync(ct);

    public async Task<string> StatusTextAsync(Guid deviceId, CancellationToken ct = default)
    {
        var d = await devices.GetDeviceAsync(deviceId, ct);
        if (d is null)
            return "Устройство не найдено.";

        var seen = d.LastSeenAt is { } ls ? $"{(DateTimeOffset.UtcNow - ls).TotalMinutes:F0} мин назад" : "никогда";
        var timeLine = d.IsUnlimited
            ? "Режим: ♾️ без ограничений (интервалы отключены)"
            : $"Осталось: {(d.TimeRemaining is { } tr ? $"{tr:hh\\:mm\\:ss}" : "—")}";
        var night = d.IsNight ? "\n🌙 Сейчас ночной режим" : "";
        var overrides = string.Concat(
            d.ForceBlocked ? "\n🚫 Принудительная блокировка" : "",
            d.Paused ? "\n⏸️ Контроль на паузе" : "");
        return $"📊 {d.Name}\n" +
               $"Статус: {d.Status ?? "нет данных"}\n" +
               $"{timeLine}{night}{overrides}\n" +
               $"Версия агента: {d.AgentVersion ?? "—"}\n" +
               $"На связи: {seen}";
    }

    public async Task<string> HistoryTextAsync(Guid deviceId, CancellationToken ct = default)
    {
        var entries = await devices.GetHistoryAsync(deviceId, 15, ct);
        if (entries.Count == 0)
            return "🧾 История пуста.";
        var lines = entries.Select(e => $"{Local(e.At):dd.MM HH:mm} — {ActionLabel(e.Action)} ({e.Actor})");
        return "🧾 История (последние действия):\n" + string.Join("\n", lines);
    }

    private static string ActionLabel(string action) => action switch
    {
        "policy.edit" => "правка политики",
        "desired.pause" => "пауза",
        "desired.resume" => "снятие паузы",
        "desired.block" => "блокировка",
        "desired.unblock" => "разблокировка",
        "desired.night_bypass" => "ночной обход",
        "device.enroll" => "привязка устройства",
        "device.revoke" => "отзыв устройства",
        "device.rename" => "переименование",
        "command.add_time" => "+время",
        "command.reset_timer" => "сброс таймера",
        "command.shutdown" => "выключение",
        "command.restart" => "перезагрузка",
        "command.update_now" => "обновление",
        _ when action.StartsWith("command.") => "команда " + action["command.".Length..],
        _ => action
    };

    public async Task<string> OverviewTextAsync(CancellationToken ct = default)
    {
        var list = await devices.ListDevicesAsync(ct);
        if (list.Count == 0)
            return "Устройств пока нет. Добавьте: /enroll.";
        var lines = list.Select(d =>
        {
            var online = d.LastSeenAt is { } ls && (DateTimeOffset.UtcNow - ls) < TimeSpan.FromMinutes(3) ? "🟢" : "⚪";
            var time = d.IsUnlimited ? "♾️ без ограничений" : $"{d.TimeRemaining:hh\\:mm\\:ss}";
            var flags = string.Concat(
                d.ForceBlocked ? " 🚫" : "",
                d.Paused ? " ⏸️" : "",
                d.IsNight ? " 🌙" : "");
            return $"{online} {d.Name} — {d.Status ?? "?"} ({time}){flags}";
        });
        return "Все устройства:\n" + string.Join("\n", lines) +
               "\n\n🚫 блок · ⏸️ пауза · 🌙 ночь · ⚪ оффлайн";
    }

    // ── Time / commands ──────────────────────────────────────────────────────
    public Task<string> AddTimeAsync(Guid deviceId, int minutes, CancellationToken ct = default)
        => EnqueueAsync(deviceId, CommandTypes.AddTime, new() { ["minutes"] = minutes.ToString() },
            $"➕ +{minutes} мин поставлено в очередь.", ct);

    public Task<string> ResetTimerAsync(Guid deviceId, CancellationToken ct = default)
        => EnqueueAsync(deviceId, CommandTypes.ResetTimer, null, "🔄 Сброс таймера поставлен в очередь.", ct);

    public Task<string> ShutdownAsync(Guid deviceId, CancellationToken ct = default)
        => EnqueueAsync(deviceId, CommandTypes.Shutdown, null, "🔌 Выключение поставлено в очередь.", ct);

    public Task<string> RestartAsync(Guid deviceId, CancellationToken ct = default)
        => EnqueueAsync(deviceId, CommandTypes.Restart, null, "🔄 Перезагрузка поставлена в очередь.", ct);

    public async Task<string> VersionTextAsync(Guid deviceId, CancellationToken ct = default)
    {
        var d = await devices.GetDeviceAsync(deviceId, ct);
        if (d is null)
            return "Устройство не найдено.";
        var target = string.IsNullOrWhiteSpace(d.TargetVersion) ? "latest" : d.TargetVersion!;
        var tracking = string.Equals(target, "latest", StringComparison.OrdinalIgnoreCase)
            ? "🆕 следит за latest"
            : $"📌 закреплена {target}";
        return $"📦 {d.Name}\n" +
               $"Текущая версия: {d.AgentVersion ?? "—"}\n" +
               $"Целевая: {tracking}";
    }

    public Task<string> RequestScreenshotAsync(Guid deviceId, string uploadId, CancellationToken ct = default)
        => EnqueueAsync(deviceId, CommandTypes.Screenshot, new() { ["uploadId"] = uploadId },
            "📷 Запрос отправлен — скриншот придёт, когда устройство онлайн (в течение TTL).", ct);

    public Task<string> UpdateNowAsync(Guid deviceId, string? tag, CancellationToken ct = default)
        => EnqueueAsync(deviceId, CommandTypes.UpdateNow,
            string.IsNullOrWhiteSpace(tag) ? null : new() { ["tag"] = tag },
            $"📦 Обновление ({tag ?? "целевая версия"}) поставлено в очередь.", ct);

    private async Task<string> EnqueueAsync(Guid deviceId, string type, Dictionary<string, string>? payload,
        string okText, CancellationToken ct)
    {
        var id = await commands.EnqueueAsync(deviceId, type, payload, CommandTtl, ct: ct);
        return id is null ? "Устройство не найдено." : okText;
    }

    // ── Overrides (desired-state) ────────────────────────────────────────────
    public Task<string> PauseAsync(Guid deviceId, bool paused, CancellationToken ct = default)
        => DesiredAsync(devices.SetPausedAsync(deviceId, paused, ct: ct),
            paused ? "⏸️ Пауза включена." : "▶️ Контроль возобновлён.");

    public Task<string> BlockAsync(Guid deviceId, bool blocked, CancellationToken ct = default)
        => DesiredAsync(devices.SetForceBlockedAsync(deviceId, blocked, ct: ct),
            blocked ? "🚫 Блокировка включена." : "✅ Разблокировано.");

    public Task<string> NightBypassAsync(Guid deviceId, DateTimeOffset? until, CancellationToken ct = default)
        => DesiredAsync(devices.SetNightBypassAsync(deviceId, until, ct: ct),
            until is null ? "🌙 Ночной обход сброшен." : $"🌙 Ночь снята до {Local(until.Value):HH:mm}.");

    // ── Policy edits ─────────────────────────────────────────────────────────
    public Task<string> SetRuleAsync(Guid deviceId, int play, int rest, CancellationToken ct = default)
        => PolicyAsync(deviceId, new PolicyPatch(PlayMinutes: play, RestMinutes: rest),
            $"✅ Правило: {play}/{rest}.", ct);

    public Task<string> SetIntervalsAsync(Guid deviceId, bool enabled, CancellationToken ct = default)
        => PolicyAsync(deviceId, new PolicyPatch(IntervalsEnabled: enabled),
            enabled ? "✅ Интервалы включены." : "♾️ Интервалы отключены.", ct);

    public Task<string> SetNightEnabledAsync(Guid deviceId, bool enabled, CancellationToken ct = default)
        => PolicyAsync(deviceId, new PolicyPatch(NightEnabled: enabled),
            enabled ? "🌙 Ночной режим включён." : "🔕 Ночной режим выключен.", ct);

    public Task<string> SetNightWindowAsync(Guid deviceId, TimeSpan start, TimeSpan end, CancellationToken ct = default)
        => PolicyAsync(deviceId, new PolicyPatch(NightEnabled: true, NightStart: start, NightEnd: end),
            $"🌙 Ночь: {start:hh\\:mm}-{end:hh\\:mm}.", ct);

    public Task<string> SetTargetVersionAsync(Guid deviceId, string target, CancellationToken ct = default)
        => PolicyAsync(deviceId, new PolicyPatch(TargetVersion: target),
            $"📦 Целевая версия: {target}.", ct);

    private async Task<string> PolicyAsync(Guid deviceId, PolicyPatch patch, string okText, CancellationToken ct)
    {
        var version = await devices.UpdatePolicyAsync(deviceId, patch, ct: ct);
        return version is null ? "Устройство не найдено." : $"{okText} (политика v{version})";
    }

    private static async Task<string> DesiredAsync(Task<int?> op, string okText)
    {
        var version = await op;
        return version is null ? "Устройство не найдено." : $"{okText} (desired v{version})";
    }

    // ── Enroll / revoke ──────────────────────────────────────────────────────
    public async Task<string> NewEnrollCodeAsync(CancellationToken ct = default)
    {
        var code = await enrollment.CreateCodeAsync(actor: "bot", ct: ct);
        return $"🔑 Код привязки: <b>{code.Code}</b>\n" +
               $"Действует до {Local(code.ExpiresAt):HH:mm}. Введите его на устройстве (Fleet:EnrollCode).";
    }

    public async Task<string> RevokeAsync(Guid deviceId, CancellationToken ct = default)
    {
        var ok = await devices.RevokeAsync(deviceId, "bot", ct);
        return ok ? "🗑️ Устройство отозвано." : "Устройство не найдено.";
    }

    public async Task<string> RenameAsync(Guid deviceId, string name, CancellationToken ct = default)
    {
        var ok = await devices.RenameAsync(deviceId, name, "bot", ct);
        return ok ? $"✏️ Имя изменено: {name.Trim()}" : "Не удалось переименовать (пустое имя или устройство не найдено).";
    }

    // ── Admins ───────────────────────────────────────────────────────────────
    public async Task<string> AdminsTextAsync(CancellationToken ct = default)
    {
        var list = await admins.ListAsync(ct);
        var lines = list.Select(a => $"• {a.ChatId}{(a.Label is null ? "" : $" ({a.Label})")}");
        return $"Администраторы ({list.Count}):\n{string.Join("\n", lines)}\n\n" +
               "Добавить: /addadmin <ID>\nУдалить: /deladmin <ID>\nСвой ID: /myid";
    }

    public async Task<string> AddAdminAsync(long chatId, CancellationToken ct = default)
        => await admins.AddAsync(chatId, ct: ct) ? $"✅ Администратор {chatId} добавлен." : $"{chatId} уже администратор.";

    public async Task<string> RemoveAdminAsync(long chatId, CancellationToken ct = default)
        => await admins.RemoveAsync(chatId, ct) ? $"✅ Администратор {chatId} удалён."
            : $"Не удалось удалить {chatId} (не найден или последний администратор).";

    public Task<bool> IsAdminAsync(long chatId, CancellationToken ct = default) => admins.IsAdminAsync(chatId, ct);
}
