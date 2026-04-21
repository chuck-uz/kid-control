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
                await SendStatusFolderAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "➕ Время")
            {
                await SendTimeFolderAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "🎮 Приложение")
            {
                await SendApplicationFolderAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "💻 Компьютер")
            {
                await SendComputerFolderAsync(chatId).ConfigureAwait(false);
                return;
            }

            if (normalized == "⚙️ Интервалы")
            {
                await SendIntervalsFolderAsync(chatId).ConfigureAwait(false);
                return;
            }
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
                case "folder_status_block":
                    await _orchestrator.HandleTelegramCommandAsync("/block", chatId).ConfigureAwait(false);
                    await UpdateStatusFolderAsync(callbackQuery).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
                    return;
                case "folder_status_unblock":
                    await _orchestrator.HandleTelegramCommandAsync("/unblock", chatId).ConfigureAwait(false);
                    await UpdateStatusFolderAsync(callbackQuery).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
                    return;
                case "folder_time_add_15":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 15).ConfigureAwait(false);
                    return;
                case "folder_time_add_30":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 30).ConfigureAwait(false);
                    return;
                case "folder_time_add_60":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 60).ConfigureAwait(false);
                    return;
                case "folder_time_add_5":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 5).ConfigureAwait(false);
                    return;
                case "folder_time_add_10":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 10).ConfigureAwait(false);
                    return;
                case "folder_time_add_20":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 20).ConfigureAwait(false);
                    return;
                case "folder_time_add_25":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 25).ConfigureAwait(false);
                    return;
                case "folder_time_add_40":
                    await ConfirmPresetAddTimeAsync(callbackQuery.Id, 40).ConfigureAwait(false);
                    return;
                case "folder_time_reset":
                    await _orchestrator.ResetSessionTimeAsync().ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Таймер сброшен").ConfigureAwait(false);
                    return;
                case "folder_app_pause":
                    await _orchestrator.PauseSystem().ConfigureAwait(false);
                    await UpdateApplicationFolderAsync(callbackQuery).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Система приостановлена").ConfigureAwait(false);
                    return;
                case "folder_app_resume":
                    await _orchestrator.ResumeSystem().ConfigureAwait(false);
                    await UpdateApplicationFolderAsync(callbackQuery).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Система возобновлена").ConfigureAwait(false);
                    return;
                case "folder_app_kill_confirm":
                    await SendHardKillConfirmAsync(chatId).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
                    return;
                case "folder_app_kill_yes":
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Останавливаю службу...").ConfigureAwait(false);
                    await _orchestrator.HardKill().ConfigureAwait(false);
                    return;
                case "folder_app_kill_cancel":
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Отменено").ConfigureAwait(false);
                    return;
                case "folder_pc_shutdown_confirm":
                    await SendPcShutdownConfirmAsync(chatId).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
                    return;
                case "folder_pc_shutdown_yes":
                    await _orchestrator.ShutdownPc().ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Команда выключения отправлена").ConfigureAwait(false);
                    return;
                case "folder_pc_shutdown_cancel":
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Отменено").ConfigureAwait(false);
                    return;
                case "folder_pc_wake":
                    await SendPcRestartConfirmAsync(chatId).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
                    return;
                case "folder_pc_restart_yes":
                    await _orchestrator.RestartPc().ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Команда перезагрузки отправлена").ConfigureAwait(false);
                    return;
                case "folder_pc_restart_cancel":
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Отменено").ConfigureAwait(false);
                    return;
                case "folder_interval_60_15":
                    await ApplyPresetRuleAsync(chatId, 60, 15).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Интервал 60/15").ConfigureAwait(false);
                    return;
                case "folder_interval_45_15":
                    await ApplyPresetRuleAsync(chatId, 45, 15).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Интервал 45/15").ConfigureAwait(false);
                    return;
                case "folder_interval_30_10":
                    await ApplyPresetRuleAsync(chatId, 30, 10).ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Интервал 30/10").ConfigureAwait(false);
                    return;
                case "folder_interval_custom":
                    _orchestrator.BeginCustomRuleInput(chatId);
                    await _botClient.SendMessage(
                            chatId: chatId,
                            text: "Введите время в формате: Работа/Отдых (например, 50/10)")
                        .ConfigureAwait(false);
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id).ConfigureAwait(false);
                    return;
                default:
                    await _botClient.AnswerCallbackQuery(callbackQuery.Id, "Неизвестное действие.", true)
                        .ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task SendMainMenuAsync(long chatId)
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "📊 Статус", "➕ Время" },
            new KeyboardButton[] { "🎮 Приложение", "💻 Компьютер" },
            new KeyboardButton[] { "⚙️ Интервалы" }
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

    private async Task SendStatusFolderAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🚫 Блок", "folder_status_block"),
                InlineKeyboardButton.WithCallbackData("✅ Разблок", "folder_status_unblock")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: _orchestrator.GetStatusDetailsText(),
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendTimeFolderAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+5 мин", "folder_time_add_5"),
                InlineKeyboardButton.WithCallbackData("+10 мин", "folder_time_add_10"),
                InlineKeyboardButton.WithCallbackData("+15 мин", "folder_time_add_15"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+20 мин", "folder_time_add_20"),
                InlineKeyboardButton.WithCallbackData("+25 мин", "folder_time_add_25"),
                InlineKeyboardButton.WithCallbackData("+30 мин", "folder_time_add_30")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("+40 мин", "folder_time_add_40"),
                InlineKeyboardButton.WithCallbackData("+1 час", "folder_time_add_60"),
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🧹 Сбросить таймер", "folder_time_reset")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "➕ Управление временем:",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendApplicationFolderAsync(long chatId)
    {
        var isPaused = _orchestrator.IsPaused();
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    isPaused ? "▶️ Включить систему" : "⏸️ Поставить на паузу",
                    isPaused ? "folder_app_resume" : "folder_app_pause"),
                InlineKeyboardButton.WithCallbackData("🛑 Полный Килл", "folder_app_kill_confirm")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: isPaused ? "🎮 Приложение (сейчас: на паузе)" : "🎮 Приложение (сейчас: активно)",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendComputerFolderAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔌 Выключить ПК", "folder_pc_shutdown_confirm"),
                InlineKeyboardButton.WithCallbackData("🔄 Перезагрузить", "folder_pc_wake")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "💻 Управление компьютером:",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendIntervalsFolderAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("60/15", "folder_interval_60_15"),
                InlineKeyboardButton.WithCallbackData("45/15", "folder_interval_45_15"),
                InlineKeyboardButton.WithCallbackData("30/10", "folder_interval_30_10")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✍️ Свой вариант", "folder_interval_custom")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "⚙️ Интервалы:",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendHardKillConfirmAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Да, убить процесс", "folder_app_kill_yes"),
                InlineKeyboardButton.WithCallbackData("Отмена", "folder_app_kill_cancel")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "⚠️ Внимание! Это полностью завершит процесс службы. Дистанционное включение будет невозможно. Приложение запустится снова только после перезагрузки компьютера или вручную админом. Вы уверены?",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendPcShutdownConfirmAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Да, выключить", "folder_pc_shutdown_yes"),
                InlineKeyboardButton.WithCallbackData("Отмена", "folder_pc_shutdown_cancel")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "Вы уверены, что хотите выключить компьютер?",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task SendPcRestartConfirmAsync(long chatId)
    {
        var inline = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Да, перезагрузить", "folder_pc_restart_yes"),
                InlineKeyboardButton.WithCallbackData("Отмена", "folder_pc_restart_cancel")
            }
        });

        await _botClient.SendMessage(
                chatId: chatId,
                text: "Вы уверены, что хотите перезагрузить компьютер?",
                replyMarkup: inline)
            .ConfigureAwait(false);
    }

    private async Task UpdateStatusFolderAsync(CallbackQuery callbackQuery)
    {
        if (callbackQuery.Message is null)
        {
            return;
        }

        var replyMarkup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🚫 Блок", "folder_status_block"),
                InlineKeyboardButton.WithCallbackData("✅ Разблок", "folder_status_unblock")
            }
        });

        await TryEditMessageTextSafeAsync(
                chatId: callbackQuery.Message.Chat.Id,
                messageId: callbackQuery.Message.MessageId,
                text: _orchestrator.GetStatusDetailsText(),
                replyMarkup: replyMarkup)
            .ConfigureAwait(false);
    }

    private async Task UpdateApplicationFolderAsync(CallbackQuery callbackQuery)
    {
        if (callbackQuery.Message is null)
        {
            return;
        }

        var isPaused = _orchestrator.IsPaused();
        var replyMarkup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    isPaused ? "▶️ Включить систему" : "⏸️ Поставить на паузу",
                    isPaused ? "folder_app_resume" : "folder_app_pause"),
                InlineKeyboardButton.WithCallbackData("🛑 Полный Килл", "folder_app_kill_confirm")
            }
        });

        await TryEditMessageTextSafeAsync(
                chatId: callbackQuery.Message.Chat.Id,
                messageId: callbackQuery.Message.MessageId,
                text: isPaused ? "🎮 Приложение (сейчас: на паузе)" : "🎮 Приложение (сейчас: активно)",
                replyMarkup: replyMarkup)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Telegram returns 400 "message is not modified" when text/markup did not change.
    /// This is a benign race and must not break the polling loop.
    /// </summary>
    private async Task TryEditMessageTextSafeAsync(
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup replyMarkup)
    {
        try
        {
            await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: text,
                    replyMarkup: replyMarkup)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Telegram edit skipped: message was not modified.");
        }
    }

    private async Task ConfirmPresetAddTimeAsync(string callbackQueryId, int minutes)
    {
        var confirmation = await _orchestrator.AddTimePresetAsync(minutes).ConfigureAwait(false);
        await _botClient.AnswerCallbackQuery(callbackQueryId, confirmation).ConfigureAwait(false);
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
