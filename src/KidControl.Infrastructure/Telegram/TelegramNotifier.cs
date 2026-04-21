using KidControl.Application.Interfaces;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using Telegram.Bot;
using Telegram.Bot.Types;

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
}
