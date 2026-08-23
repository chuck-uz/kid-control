using KidControl.Application.Abstractions;
using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Contracts;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
// Disambiguate the Telegram Update type from the KidControl.Infrastructure.Update namespace.
using TgUpdate = Telegram.Bot.Types.Update;

namespace KidControl.Infrastructure.Telegram;

/// <summary>
/// Long-polling Telegram listener. A persistent reply keyboard exposes six folders; each
/// folder opens an inline sub-menu whose buttons carry slash-commands (routed through the
/// pure <see cref="CommandParser"/> into <see cref="SessionService.ExecuteAsync"/>) or a
/// few UI-only tokens (menu:*/pc:*/ver:*) handled here.
///
/// Hardening: pending updates queued while the service was down are dropped on startup
/// (a stale "/shutdown" must not replay), and non-admin chats are ignored outright.
/// </summary>
public sealed class TelegramBotBackgroundService(
    ITelegramBotClient bot,
    SessionService session,
    IUpdateService updates,
    KidControl.Infrastructure.Ipc.UiCommandClient ui,
    IOptions<TelegramConfig> config,
    ILogger<TelegramBotBackgroundService> logger) : BackgroundService
{
    private readonly TelegramConfig _config = config.Value;

    // Persistent bottom keyboard (six folders). Sending it replaces any stale keyboard
    // left over from a previous version.
    private static readonly ReplyKeyboardMarkup MainKeyboard = new(new[]
    {
        new KeyboardButton[] { "📊 Статус", "➕ Время" },
        new KeyboardButton[] { "🎮 Приложение", "💻 Компьютер" },
        new KeyboardButton[] { "⚙️ Интервалы", "📦 Версия" }
    })
    { ResizeKeyboard = true };

    private static readonly InlineKeyboardMarkup StatusMenu = new(new[]
    {
        new[] { Btn("🚫 Блок", "/block"), Btn("✅ Разблок", "/unblock") },
        new[] { Btn("🔄 Сбросить таймер", "/reset") }
    });

    private static readonly InlineKeyboardMarkup TimeMenu = new(new[]
    {
        new[] { Btn("+15", "/addtime 15"), Btn("+30", "/addtime 30") },
        new[] { Btn("+60", "/addtime 60"), Btn("+120", "/addtime 120") }
    });

    private static readonly InlineKeyboardMarkup AppMenu = new(new[]
    {
        new[] { Btn("⏸️ Пауза", "/pause"), Btn("▶️ Продолжить", "/resume") }
    });

    private static readonly InlineKeyboardMarkup PcMenu = new(new[]
    {
        new[] { Btn("📷 Скриншот экрана", "ui:screenshot") },
        new[] { Btn("🔌 Выключить ПК", "pc:shutdown") },
        new[] { Btn("🔄 Перезагрузить ПК", "pc:restart") }
    });

    private static readonly InlineKeyboardMarkup RulesMenu = new(new[]
    {
        new[] { Btn("60 / 15", "/setrule 60 15"), Btn("45 / 15", "/setrule 45 15") },
        new[] { Btn("40 / 20", "/setrule 40 20"), Btn("30 / 10", "/setrule 30 10") },
        new[] { Btn("♾️ Отключить интервалы", "/intervals off") },
        new[] { Btn("✅ Включить интервалы", "/intervals on") }
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.IsConfigured)
        {
            logger.LogWarning("Telegram listener disabled: not configured.");
            return;
        }

        var offset = await DropPendingAsync(stoppingToken).ConfigureAwait(false);
        logger.LogInformation("Telegram listener started (offset {Offset}).", offset);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updateList = await bot.GetUpdates(offset, timeout: 20, cancellationToken: stoppingToken).ConfigureAwait(false);
                foreach (var update in updateList)
                {
                    offset = update.Id + 1;
                    await HandleUpdateSafeAsync(update, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram poll iteration failed.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        logger.LogInformation("Telegram listener stopped.");
    }

    private async Task<int> DropPendingAsync(CancellationToken ct)
    {
        try
        {
            var latest = await bot.GetUpdates(offset: -1, timeout: 0, cancellationToken: ct).ConfigureAwait(false);
            return latest.Length > 0 ? latest[^1].Id + 1 : 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to drop pending updates; starting from 0.");
            return 0;
        }
    }

    private async Task HandleUpdateSafeAsync(TgUpdate update, CancellationToken ct)
    {
        try
        {
            if (update is { Type: UpdateType.Message, Message: { } message })
            {
                if (message.Text is { } text)
                {
                    await HandleMessageAsync(message.Chat.Id, text, ct).ConfigureAwait(false);
                }
                else if (message.Voice is { } voice)
                {
                    await HandleAudioAsync(message.Chat.Id, voice.FileId, "voice.ogg", ct).ConfigureAwait(false);
                }
                else if (message.Audio is { } audio)
                {
                    await HandleAudioAsync(message.Chat.Id, audio.FileId, audio.FileName ?? "audio.mp3", ct).ConfigureAwait(false);
                }
                else if (message.Document is { } doc && (doc.MimeType?.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    await HandleAudioAsync(message.Chat.Id, doc.FileId, doc.FileName ?? "audio", ct).ConfigureAwait(false);
                }
            }
            else if (update is { Type: UpdateType.CallbackQuery, CallbackQuery: { } callback })
            {
                await HandleCallbackAsync(callback, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle update {UpdateId}.", update.Id);
        }
    }

    private async Task HandleMessageAsync(long chatId, string text, CancellationToken ct)
    {
        if (!_config.IsAdmin(chatId))
        {
            logger.LogWarning("Ignored message from non-admin chat {ChatId}.", chatId);
            return;
        }

        var t = text.Trim();

        // Folder buttons (and /start) — open the matching inline sub-menu.
        switch (t)
        {
            case "/start":
            case "/menu":
                await Send(chatId, "Управление KidControl:", MainKeyboard, ct).ConfigureAwait(false);
                return;
            case "📊 Статус":
                await Send(chatId, session.StatusText(), StatusMenu, ct).ConfigureAwait(false);
                return;
            case "➕ Время":
                await Send(chatId, "Добавить игровое время:", TimeMenu, ct).ConfigureAwait(false);
                return;
            case "🎮 Приложение":
                await Send(chatId, "Управление контролем:", AppMenu, ct).ConfigureAwait(false);
                return;
            case "💻 Компьютер":
                await Send(chatId, "Управление компьютером:", PcMenu, ct).ConfigureAwait(false);
                return;
            case "⚙️ Интервалы":
                await Send(chatId, "Выберите режим (игра / отдых, мин):", RulesMenu, ct).ConfigureAwait(false);
                return;
            case "📦 Версия":
                await Send(chatId, $"Текущая версия: {updates.CurrentVersion}", VersionMenu(), ct).ConfigureAwait(false);
                return;
        }

        // Anything else: treat as a typed command; refresh the keyboard on the reply.
        var reply = await session.ExecuteAsync(CommandParser.Parse(t), ct).ConfigureAwait(false);
        await Send(chatId, reply, MainKeyboard, ct).ConfigureAwait(false);
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message?.Chat.Id ?? callback.From.Id;
        if (!_config.IsAdmin(chatId))
        {
            await bot.AnswerCallbackQuery(callback.Id, "Недостаточно прав.", showAlert: true, cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        var data = callback.Data ?? string.Empty;

        // UI-only tokens handled here; everything else is a slash command.
        switch (data)
        {
            case "pc:shutdown":
                await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct).ConfigureAwait(false);
                await Send(chatId, "Выключить компьютер?", Confirm("⚠️ Да, выключить", "/shutdown"), ct).ConfigureAwait(false);
                return;
            case "pc:restart":
                await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct).ConfigureAwait(false);
                await Send(chatId, "Перезагрузить компьютер?", Confirm("⚠️ Да, перезагрузить", "/restart"), ct).ConfigureAwait(false);
                return;
            case "menu:cancel":
                await bot.AnswerCallbackQuery(callback.Id, "Отменено", cancellationToken: ct).ConfigureAwait(false);
                return;
            case "ui:screenshot":
                await bot.AnswerCallbackQuery(callback.Id, "Делаю скриншот…", cancellationToken: ct).ConfigureAwait(false);
                await SendScreenshotAsync(chatId, ct).ConfigureAwait(false);
                return;
            case "ver:check":
                await bot.AnswerCallbackQuery(callback.Id, "Проверяю…", cancellationToken: ct).ConfigureAwait(false);
                var info = await updates.CheckAsync(ct).ConfigureAwait(false);
                var msg = info is null
                    ? $"Обновлений нет. Текущая версия: {updates.CurrentVersion}."
                    : $"Доступна версия {info.Tag}. Обновление установится автоматически.";
                await Send(chatId, msg, MainKeyboard, ct).ConfigureAwait(false);
                return;
        }

        var reply = await session.ExecuteAsync(CommandParser.Parse(data), ct).ConfigureAwait(false);
        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct).ConfigureAwait(false);
        await Send(chatId, reply, MainKeyboard, ct).ConfigureAwait(false);
    }

    private async Task SendScreenshotAsync(long chatId, CancellationToken ct)
    {
        var path = await ui.CaptureScreenshotAsync(ct).ConfigureAwait(false);
        if (path is null)
        {
            await Send(chatId, "Не удалось сделать скриншот (UI не запущен?).", MainKeyboard, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var fs = File.OpenRead(path);
            await bot.SendPhoto(chatId, InputFile.FromStream(fs, "screen.png"), caption: "Скриншот экрана", cancellationToken: ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private async Task HandleAudioAsync(long chatId, string fileId, string suggestedName, CancellationToken ct)
    {
        if (!_config.IsAdmin(chatId))
        {
            logger.LogWarning("Ignored audio from non-admin chat {ChatId}.", chatId);
            return;
        }

        var ext = Path.GetExtension(suggestedName);
        if (string.IsNullOrWhiteSpace(ext)) { ext = ".ogg"; }
        var dest = TransferPaths.NewFile(ext);

        try
        {
            await using (var fs = File.Create(dest))
            {
                await bot.GetInfoAndDownloadFile(fileId, fs, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download audio {FileId}.", fileId);
            await Send(chatId, "Не удалось скачать аудио.", MainKeyboard, ct).ConfigureAwait(false);
            return;
        }

        var ok = await ui.PlayAudioAsync(dest, ct).ConfigureAwait(false);
        await Send(chatId, ok ? "▶️ Воспроизвожу на ПК." : "Не удалось воспроизвести (UI не запущен или формат не поддерживается).", MainKeyboard, ct)
            .ConfigureAwait(false);
    }

    private Task Send(long chatId, string text, ReplyMarkup markup, CancellationToken ct)
        => bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);

    private static InlineKeyboardMarkup VersionMenu()
        => new(new[] { new[] { Btn("⬆️ Проверить обновление", "ver:check") } });

    private static InlineKeyboardMarkup Confirm(string yesLabel, string yesCommand)
        => new(new[] { new[] { Btn(yesLabel, yesCommand), Btn("Отмена", "menu:cancel") } });

    private static InlineKeyboardButton Btn(string label, string data)
        => InlineKeyboardButton.WithCallbackData(label, data);
}
