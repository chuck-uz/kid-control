using System.Net;
using KidControl.Application.Abstractions;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace KidControl.Infrastructure.Telegram;

/// <summary>
/// Outbound Telegram messaging (<see cref="ITelegramGateway"/>) over Telegram.Bot.
///
/// Security note: release tags and notes originate from GitHub and are HTML-escaped
/// before being sent with <see cref="ParseMode.Html"/> — the original interpolated them
/// raw, allowing markup/tag injection into admin notifications.
/// </summary>
public sealed class TelegramGateway : ITelegramGateway
{
    private const int MaxNotesLength = 400;

    private readonly ITelegramBotClient _bot;
    private readonly TelegramConfig _config;
    private readonly ILogger<TelegramGateway> _logger;

    public TelegramGateway(
        ITelegramBotClient bot,
        IOptions<TelegramConfig> config,
        ILogger<TelegramGateway> logger)
    {
        _bot = bot;
        _config = config.Value;
        _logger = logger;
    }

    public async Task SendReplyAsync(long chatId, string message, CancellationToken ct = default)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        try
        {
            await _bot.SendMessage(chatId: chatId, text: message, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send reply to chat {ChatId}.", chatId);
        }
    }

    public async Task BroadcastAsync(string message, CancellationToken ct = default)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        foreach (var chatId in _config.AdminChatIds)
        {
            try
            {
                await _bot.SendMessage(chatId: chatId, text: message, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast to chat {ChatId}.", chatId);
            }
        }
    }

    public async Task NotifyUpdateAvailableAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        var tag = WebUtility.HtmlEncode(info.Tag);
        var size = info.AssetSize > 0 ? $" ({info.AssetSize / 1024.0 / 1024.0:F1} МБ)" : string.Empty;

        var notesBody = info.ReleaseNotes.Length > MaxNotesLength
            ? info.ReleaseNotes[..MaxNotesLength] + "…"
            : info.ReleaseNotes;
        var notes = string.IsNullOrWhiteSpace(notesBody)
            ? string.Empty
            : $"\n\n{WebUtility.HtmlEncode(notesBody)}";

        var text = $"🆕 Доступна новая версия KidControl\n\nНовая: <b>{tag}</b>{size}{notes}";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⬆️ Обновить сейчас", $"update_install_{info.Tag}"),
                InlineKeyboardButton.WithCallbackData("⏰ Позже", "update_later")
            }
        });

        foreach (var chatId in _config.AdminChatIds)
        {
            try
            {
                await _bot.SendMessage(
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

    public async Task SendPhotoAsync(long chatId, Stream photo, string? caption, CancellationToken ct = default)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        try
        {
            await _bot.SendPhoto(
                    chatId: chatId,
                    photo: InputFile.FromStream(photo, "screenshot.png"),
                    caption: caption,
                    cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send photo to chat {ChatId}.", chatId);
        }
    }

    private bool EnsureConfigured()
    {
        if (_config.IsConfigured)
        {
            return true;
        }

        _logger.LogWarning("Telegram gateway is not configured; message skipped.");
        return false;
    }
}
