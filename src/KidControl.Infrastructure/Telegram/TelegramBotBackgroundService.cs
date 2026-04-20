using KidControl.Application.Services;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Context;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;

namespace KidControl.Infrastructure.Telegram;

public sealed class TelegramBotBackgroundService : BackgroundService
{
    private readonly TelegramBotClient _botClient;
    private readonly SessionOrchestrator _orchestrator;
    private readonly TelegramConfig _config;
    private readonly ILogger<TelegramBotBackgroundService> _logger;

    public TelegramBotBackgroundService(
        TelegramBotClient botClient,
        SessionOrchestrator orchestrator,
        IOptions<TelegramConfig> config,
        ILogger<TelegramBotBackgroundService> logger)
    {
        _botClient = botClient;
        _orchestrator = orchestrator;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.BotToken) || _config.AdminChatIds.Length == 0)
        {
            _logger.LogWarning("Telegram bot background service is disabled due to missing configuration.");
            return;
        }

        _logger.LogInformation("Telegram bot listener started.");

        var offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _botClient.GetUpdates(
                        offset: offset,
                        timeout: 20,
                        cancellationToken: stoppingToken)
                    .ConfigureAwait(false);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;

                    if (update.Type == UpdateType.Message && update.Message?.Text is not null)
                    {
                        await HandleMessageAsync(update.Message.Chat.Id, update.Message.Text).ConfigureAwait(false);
                        continue;
                    }

                    if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is not null)
                    {
                        await HandleCallbackAsync(update.CallbackQuery).ConfigureAwait(false);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram listener iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram bot listener stopped.");
    }

    private async Task HandleMessageAsync(long chatId, string text)
    {
        if (!IsAdmin(chatId))
        {
            _logger.LogWarning("Telegram message ignored from non-admin chat {ChatId}.", chatId);
            return;
        }

        _logger.LogInformation("Telegram command received: {Text}", text);
        var normalized = text.Trim();
        using (LogContext.PushProperty("ChatId", chatId))
        {
            if (await _orchestrator.TryHandleCustomRuleInputAsync(chatId, normalized).ConfigureAwait(false))
            {
                return;
            }
            if (await _orchestrator.TryHandleNightModeInputAsync(chatId, normalized).ConfigureAwait(false))
            {
                return;
            }

            if (normalized.Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                await SendMainMenuAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "📊 Статус")
            {
                await _orchestrator.HandleTelegramCommandAsync("/status", chatId).ConfigureAwait(false);
                await SendStatusInlineActionsAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "➕ Добавить время")
            {
                await SendAddTimeInlineActionsAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "🚫 Блок")
            {
                await _orchestrator.HandleTelegramCommandAsync("/block", chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "✅ Разблок")
            {
                await _orchestrator.HandleTelegramCommandAsync("/unblock", chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "💀 Выключить систему")
            {
                await SendShutdownConfirmAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "🔌 Выключить ПК")
            {
                await SendPcShutdownConfirmAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "🔄 Перезагрузить ПК")
            {
                await SendPcRestartConfirmAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "⚙️ Настройки")
            {
                await SendSettingsInlineActionsAsync(chatId).ConfigureAwait(false);
                return;
            }

            await _orchestrator.HandleTelegramCommandAsync(normalized, chatId).ConfigureAwait(false);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callbackQuery)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        if (!IsAdmin(chatId))
        {
            _logger.LogWarning("Telegram callback ignored from non-admin chat {ChatId}.", chatId);
            await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Недостаточно прав.", true).ConfigureAwait(false);
            return;
        }

        using (LogContext.PushProperty("ChatId", chatId))
        {
            switch (callbackQuery.Data)
            {
                case "status_add_15":
                    await _orchestrator.HandleTelegramCommandAsync("/addtime 15", chatId).ConfigureAwait(false);
                    break;
                case "status_add_10":
                    await _orchestrator.HandleTelegramCommandAsync("/addtime 10", chatId).ConfigureAwait(false);
                    break;
                case "status_add_5":
                    await _orchestrator.HandleTelegramCommandAsync("/addtime 5", chatId).ConfigureAwait(false);
                    break;
                case "status_add_60":
                    await _orchestrator.HandleTelegramCommandAsync("/addtime 60", chatId).ConfigureAwait(false);
                    break;
                case "status_add_30":
                    await _orchestrator.HandleTelegramCommandAsync("/addtime 30", chatId).ConfigureAwait(false);
                    break;
                case "status_setrule_60_15":
                    await _orchestrator.HandleTelegramCommandAsync("/setrule 60 15", chatId).ConfigureAwait(false);
                    break;
                case "settings_60_15":
                    await ApplyPresetRuleAsync(chatId, 60, 15).ConfigureAwait(false);
                    break;
                case "settings_45_15":
                    await ApplyPresetRuleAsync(chatId, 45, 15).ConfigureAwait(false);
                    break;
                case "settings_30_10":
                    await ApplyPresetRuleAsync(chatId, 30, 10).ConfigureAwait(false);
                    break;
                case "settings_custom":
                    _orchestrator.BeginCustomRuleInput(chatId);
                    await _botClient.SendMessage(
                            chatId: chatId,
                            text: "Введите время в формате: Работа/Отдых (например, 50/10)")
                        .ConfigureAwait(false);
                    break;
                case "settings_night":
                    _orchestrator.BeginNightModeInput(chatId);
                    await _botClient.SendMessage(
                            chatId: chatId,
                            text: "Введите ночной интервал в формате 21:30-08:00")
                        .ConfigureAwait(false);
                    break;
                case "pc_shutdown_confirm":
                    await _orchestrator.ShutdownPc().ConfigureAwait(false);
                    break;
                case "pc_restart_confirm":
                    await _orchestrator.RestartPc().ConfigureAwait(false);
                    break;
                case "shutdown_confirm":
                    await _orchestrator.ExecuteRemoteShutdownAsync(chatId).ConfigureAwait(false);
                    break;
                default:
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Неизвестное действие.", true)
                        .ConfigureAwait(false);
                    return;
            }
        }

        await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
    }

    private async Task SendMainMenuAsync(long chatId)
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "📊 Статус", "➕ Добавить время" },
            new KeyboardButton[] { "🚫 Блок", "✅ Разблок" },
            new KeyboardButton[] { "⚙️ Настройки", "🔌 Выключить ПК" },
            new KeyboardButton[] { "🔄 Перезагрузить ПК" },
            new KeyboardButton[] { "💀 Выключить систему" }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };

        await _botClient.SendMessage(
                chatId: chatId,
                text: "Управление KidControl:",
                replyMarkup: keyboard)
            .ConfigureAwait(false);
    }

    private async Task SendSettingsInlineActionsAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🟢 60 / 15", "settings_60_15"),
                InlineKeyboardButton.WithCallbackData("🟡 45 / 15", "settings_45_15")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🟠 30 / 10", "settings_30_10"),
                InlineKeyboardButton.WithCallbackData("✍️ Свой вариант", "settings_custom")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🌙 Ночное время", "settings_night")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "⚙️ Выберите правило времени:",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendShutdownConfirmAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithCallbackData("⚠️ ПОДТВЕРЖДАЮ, ВЫКЛЮЧИТЬ", "shutdown_confirm")
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "⚡ Подтвердите полное отключение приложения.",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendPcShutdownConfirmAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithCallbackData("⚠️ ДА, ВЫКЛЮЧИТЬ КОМПЬЮТЕР", "pc_shutdown_confirm")
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "Подтвердите выключение ПК.",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendPcRestartConfirmAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithCallbackData("⚠️ ДА, ПЕРЕЗАГРУЗИТЬ КОМПЬЮТЕР", "pc_restart_confirm")
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "Подтвердите перезагрузку ПК.",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendStatusInlineActionsAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+5 мин", "status_add_5"),
                InlineKeyboardButton.WithCallbackData("+10 мин", "status_add_10"),
                InlineKeyboardButton.WithCallbackData("+15 мин", "status_add_15"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+60 мин", "status_add_60")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Установить 60/15", "status_setrule_60_15")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "📊 Быстрые действия:",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendAddTimeInlineActionsAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+5 мин", "status_add_5"),
                InlineKeyboardButton.WithCallbackData("+10 мин", "status_add_10"),
                InlineKeyboardButton.WithCallbackData("+15 мин", "status_add_15")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+30 мин", "status_add_30"),
                InlineKeyboardButton.WithCallbackData("+60 мин", "status_add_60")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "➕ Выберите, сколько добавить:",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private bool IsAdmin(long chatId)
    {
        return _config.AdminChatIds.Contains(chatId);
    }

    private async Task ApplyPresetRuleAsync(long chatId, int workMinutes, int restMinutes)
    {
        var confirmation = await _orchestrator
            .UpdateRules(TimeSpan.FromMinutes(workMinutes), TimeSpan.FromMinutes(restMinutes))
            .ConfigureAwait(false);

        await _botClient.SendMessage(chatId: chatId, text: confirmation).ConfigureAwait(false);
    }
}
