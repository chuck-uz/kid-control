using KidControl.Application.Interfaces;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace KidControl.Infrastructure.Telegram;

public sealed class TelegramNotifier : ITelegramNotifier
{
    private readonly TelegramBotClient _botClient;
    private readonly TelegramConfig _config;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(
        TelegramBotClient botClient,
        IOptions<TelegramConfig> config,
        ILogger<TelegramNotifier> logger)
    {
        _botClient = botClient;
        _logger = logger;
        _config = config.Value;
    }

    public async Task SendReplyAsync(long chatId, string message)
    {
        if (string.IsNullOrWhiteSpace(_config.BotToken))
        {
            _logger.LogWarning("Telegram reply skipped: BotToken is not configured.");
            return;
        }

        await _botClient.SendMessage(
                chatId: chatId,
                text: message)
            .ConfigureAwait(false);
    }

    public async Task BroadcastAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(_config.BotToken))
        {
            _logger.LogWarning("Telegram broadcast skipped: BotToken is not configured.");
            return;
        }

        if (_config.AdminChatIds.Length == 0)
        {
            _logger.LogWarning("Telegram broadcast skipped: no admin chat ids configured.");
            return;
        }

        foreach (var adminChatId in _config.AdminChatIds)
        {
            await _botClient.SendMessage(
                    chatId: adminChatId,
                    text: message)
                .ConfigureAwait(false);
        }
    }

    public async Task SendPhotoAsync(long chatId, string filePath, string? caption = null)
    {
        if (string.IsNullOrWhiteSpace(_config.BotToken))
        {
            _logger.LogWarning("Telegram photo skipped: BotToken is not configured.");
            return;
        }

        await using var stream = File.OpenRead(filePath);
        await _botClient.SendPhoto(
                chatId: chatId,
                photo: InputFile.FromStream(stream, Path.GetFileName(filePath)),
                caption: caption)
            .ConfigureAwait(false);
    }

    public async Task NotifyUpdateAvailableAsync(UpdateInfoDto info, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.BotToken) || _config.AdminChatIds.Length == 0)
        {
            _logger.LogWarning("Update notification skipped: bot not configured.");
            return;
        }

        var mb = info.AssetSize > 0 ? $"{info.AssetSize / 1024.0 / 1024.0:F1} МБ" : string.Empty;
        var sizeStr = mb.Length > 0 ? $" ({mb})" : string.Empty;
        var notesBody = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? string.Empty
            : (info.ReleaseNotes.Length > 400 ? info.ReleaseNotes[..400] + "…" : info.ReleaseNotes);
        var notes = notesBody.Length > 0 ? $"\n\n{notesBody}" : string.Empty;

        var text = $"🆕 Доступна новая версия KidControl\n\n" +
                   $"Текущая: ? → Новая: <b>{info.Tag}</b>{sizeStr}" +
                   $"\nОпубликована: {info.PublishedAt.ToLocalTime():dd.MM.yyyy HH:mm}" +
                   notes;

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⬆️ Обновить сейчас", $"update_install_{info.Tag}"),
                InlineKeyboardButton.WithCallbackData("⏰ Позже", $"update_later_{info.Tag}"),
            }
        });

        foreach (var chatId in _config.AdminChatIds)
        {
            try
            {
                await _botClient.SendMessage(
                        chatId: chatId,
                        text: text,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send update notification to chat {ChatId}.", chatId);
            }
        }
    }
}
