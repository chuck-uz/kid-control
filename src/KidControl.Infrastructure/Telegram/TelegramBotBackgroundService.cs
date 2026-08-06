using KidControl.Application.Commands;
using KidControl.Application.Services;
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
/// Long-polling Telegram listener. Admin chats issue commands (typed or via the inline
/// menu); every input flows through the pure <see cref="CommandParser"/> into
/// <see cref="SessionService.ExecuteAsync"/>, so message and callback handling share one
/// tiny dispatch path instead of the original's ~600-line switch.
///
/// Hardening: pending updates queued while the service was down are dropped on startup
/// (a stale "/shutdown" must not replay), and non-admin chats are ignored outright.
/// </summary>
public sealed class TelegramBotBackgroundService(
    ITelegramBotClient bot,
    SessionService session,
    IOptions<TelegramConfig> config,
    ILogger<TelegramBotBackgroundService> logger) : BackgroundService
{
    private static readonly InlineKeyboardMarkup Menu = new(new[]
    {
        new[] { Button("📊 Статус", "/status"), Button("🔄 Сброс", "/reset") },
        new[] { Button("🚫 Блок", "/block"), Button("✅ Разблок", "/unblock") },
        new[] { Button("+15", "/addtime 15"), Button("+30", "/addtime 30"), Button("+60", "/addtime 60") },
        new[] { Button("⏸️ Пауза", "/pause"), Button("▶️ Продолжить", "/resume") }
    });

    private readonly TelegramConfig _config = config.Value;

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
                var updates = await bot.GetUpdates(offset, timeout: 20, cancellationToken: stoppingToken).ConfigureAwait(false);
                foreach (var update in updates)
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
            if (update is { Type: UpdateType.Message, Message: { Text: { } text } message })
            {
                await HandleMessageAsync(message.Chat.Id, text, ct).ConfigureAwait(false);
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

        var trimmed = text.Trim();
        if (trimmed is "/start" or "/menu")
        {
            await bot.SendMessage(chatId, "Управление KidControl:", replyMarkup: Menu, cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        var reply = await session.ExecuteAsync(CommandParser.Parse(trimmed), ct).ConfigureAwait(false);
        await bot.SendMessage(chatId, reply, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message?.Chat.Id ?? callback.From.Id;
        if (!_config.IsAdmin(chatId))
        {
            logger.LogWarning("Ignored callback from non-admin chat {ChatId}.", chatId);
            await bot.AnswerCallbackQuery(callback.Id, "Недостаточно прав.", showAlert: true, cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        var reply = await session.ExecuteAsync(CommandParser.Parse(callback.Data), ct).ConfigureAwait(false);
        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct).ConfigureAwait(false);
        await bot.SendMessage(chatId, reply, cancellationToken: ct).ConfigureAwait(false);
    }

    private static InlineKeyboardButton Button(string label, string command)
        => InlineKeyboardButton.WithCallbackData(label, command);
}
