using System.Diagnostics;
using KidControl.Application.Interfaces;
using KidControl.Contracts;
using KidControl.Domain.Entities;
using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;

namespace KidControl.Application.Services;

public sealed class SessionOrchestrator
{
    private readonly object _sync = new();
    private readonly HashSet<long> _awaitingCustomRuleInput = [];
    private readonly ComputerSession _session;
    private readonly IUiNotifier _uiNotifier;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;
    private string? _pendingOtp;
    private DateTimeOffset _otpExpiresAt;

    public SessionOrchestrator(
        IUiNotifier uiNotifier,
        ITelegramNotifier telegramNotifier,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _uiNotifier = uiNotifier ?? throw new ArgumentNullException(nameof(uiNotifier));
        _telegramNotifier = telegramNotifier ?? throw new ArgumentNullException(nameof(telegramNotifier));
        _hostApplicationLifetime = hostApplicationLifetime;

        _session = new ComputerSession();
        _session.SetRule(new ScheduleRule(playMinutes: 60, restMinutes: 15));
        _session.AddTime(TimeSpan.FromMinutes(60));
    }

    public async Task ProcessTickAsync()
    {
        SessionStateDto state;
        LockStatus previousStatus;
        LockStatus newStatus;
        lock (_sync)
        {
            previousStatus = _session.CurrentStatus;
            _session.Tick(TimeSpan.FromSeconds(1));
            newStatus = _session.CurrentStatus;
            state = ToDto();
        }

        await _uiNotifier.NotifyStateChangedAsync(state).ConfigureAwait(false);

        if (previousStatus == LockStatus.Active && newStatus == LockStatus.Blocked)
        {
            await _telegramNotifier
                .BroadcastAsync("⚡ Время игры вышло. Компьютер перешел в режим блокировки.")
                .ConfigureAwait(false);
        }
    }

    public async Task HandleTelegramCommandAsync(string text, long chatId)
    {
        var commandText = (text ?? string.Empty).Trim();

        if (commandText.Length == 0)
        {
            await _telegramNotifier.SendReplyAsync(chatId, "Команда пустая.").ConfigureAwait(false);
            return;
        }

        if (await TryHandleCustomRuleInputAsync(chatId, commandText).ConfigureAwait(false))
        {
            return;
        }

        var firstToken = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        if (firstToken == "/setrule")
        {
            var (ok, workMinutes, restMinutes) = TryParseSetRuleCommand(commandText);
            if (!ok)
            {
                await _telegramNotifier
                    .SendReplyAsync(chatId, "Формат: /setrule [игра_мин] [отдых_мин].")
                    .ConfigureAwait(false);
                return;
            }

            var message = await UpdateRules(
                    TimeSpan.FromMinutes(workMinutes),
                    TimeSpan.FromMinutes(restMinutes))
                .ConfigureAwait(false);

            await _telegramNotifier.SendReplyAsync(chatId, message).ConfigureAwait(false);
            return;
        }

        string reply;
        SessionStateDto state;
        lock (_sync)
        {
            reply = firstToken switch
            {
                "/status" => BuildStatusMessage(),
                "/block" => HandleBlock(),
                "/unblock" => HandleUnblock(),
                "/addtime" => HandleAddTime(commandText),
                "⚙️" => "Нажмите кнопку «⚙️ Настройки».",
                _ => "Неизвестная команда. Доступно: /status, /block, /unblock, /addtime [мин], /setrule [игра] [отдых]."
            };

            state = ToDto();
        }

        await _telegramNotifier.SendReplyAsync(chatId, reply).ConfigureAwait(false);
        await _uiNotifier.NotifyStateChangedAsync(state).ConfigureAwait(false);
    }

    public async Task<string> UpdateRules(TimeSpan workTime, TimeSpan restTime)
    {
        if (workTime <= TimeSpan.Zero || restTime <= TimeSpan.Zero)
        {
            return "❌ Время должно быть больше нуля.";
        }

        if (workTime.TotalMinutes > int.MaxValue || restTime.TotalMinutes > int.MaxValue)
        {
            return "❌ Слишком большое значение времени.";
        }

        SessionStateDto state;
        lock (_sync)
        {
            var rule = new ScheduleRule((int)workTime.TotalMinutes, (int)restTime.TotalMinutes);
            _session.SetRuleAndResetPhase(rule);
            state = ToDto();
        }

        await _uiNotifier.NotifyStateChangedAsync(state).ConfigureAwait(false);
        return $"✅ Новое правило установлено: {(int)workTime.TotalMinutes} мин работы и {(int)restTime.TotalMinutes} мин отдыха. Таймер на ПК обновлен.";
    }

    public void BeginCustomRuleInput(long chatId)
    {
        lock (_sync)
        {
            _awaitingCustomRuleInput.Add(chatId);
        }
    }

    public SessionStateDto GetCurrentState()
    {
        lock (_sync)
        {
            return ToDto();
        }
    }

    public async Task GenerateAndSendOtpAsync()
    {
        var otp = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
        lock (_sync)
        {
            _pendingOtp = otp;
            _otpExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        }

        await _telegramNotifier
            .BroadcastAsync($"Код подтверждения аварийного отключения: {otp}. Действителен 5 минут.")
            .ConfigureAwait(false);
    }

    public bool ValidateEmergencyOtp(string otp)
    {
        if (string.IsNullOrWhiteSpace(otp))
        {
            return false;
        }

        lock (_sync)
        {
            if (_pendingOtp is null || DateTimeOffset.UtcNow > _otpExpiresAt)
            {
                return false;
            }

            var isValid = string.Equals(_pendingOtp, otp.Trim(), StringComparison.Ordinal);
            if (isValid)
            {
                _pendingOtp = null;
            }

            return isValid;
        }
    }

    public async Task ExecuteRemoteShutdownAsync(long chatId)
    {
        await _telegramNotifier
            .SendReplyAsync(chatId, "⚡ Выключение системы подтверждено. Приложение завершает работу.")
            .ConfigureAwait(false);

        foreach (var process in Process.GetProcessesByName("KidControl.UiHost"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort: service shutdown proceeds regardless.
            }
        }

        _hostApplicationLifetime?.StopApplication();
    }

    private string HandleBlock()
    {
        _session.BlockForRest();
        return "Компьютер заблокирован. Запущен таймер перерыва.";
    }

    private string HandleUnblock()
    {
        _session.ForceUnblock();
        return "Компьютер разблокирован.";
    }

    private string HandleAddTime(string commandText)
    {
        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var minutes) || minutes <= 0)
        {
            return "Формат: /addtime [минуты], где минуты > 0.";
        }

        _session.AddTime(TimeSpan.FromMinutes(minutes));
        return $"Добавлено {minutes} минут.";
    }

    public async Task<bool> TryHandleCustomRuleInputAsync(long chatId, string input)
    {
        bool isAwaiting;
        lock (_sync)
        {
            isAwaiting = _awaitingCustomRuleInput.Contains(chatId);
        }

        if (!isAwaiting)
        {
            return false;
        }

        var parts = input.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var workMinutes) ||
            !int.TryParse(parts[1], out var restMinutes) ||
            workMinutes <= 0 ||
            restMinutes <= 0)
        {
            await _telegramNotifier
                .SendReplyAsync(chatId, "❌ Неверный формат. Введите: Работа/Отдых (например, 50/10).")
                .ConfigureAwait(false);
            return true;
        }

        lock (_sync)
        {
            _awaitingCustomRuleInput.Remove(chatId);
        }

        var message = await UpdateRules(
                TimeSpan.FromMinutes(workMinutes),
                TimeSpan.FromMinutes(restMinutes))
            .ConfigureAwait(false);
        await _telegramNotifier.SendReplyAsync(chatId, message).ConfigureAwait(false);
        return true;
    }

    private static (bool ok, int workMinutes, int restMinutes) TryParseSetRuleCommand(string commandText)
    {
        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !int.TryParse(parts[1], out var workMinutes) ||
            !int.TryParse(parts[2], out var restMinutes) ||
            workMinutes <= 0 ||
            restMinutes <= 0)
        {
            return (false, 0, 0);
        }

        return (true, workMinutes, restMinutes);
    }

    private string BuildStatusMessage()
    {
        var state = ToDto();
        return $"Статус: {GetStatusEmoji(state.Status)} {state.Status}. Осталось: {state.TimeRemaining:c}.";
    }

    private SessionStateDto ToDto()
    {
        return new SessionStateDto(
            Status: _session.CurrentStatus.ToString(),
            TimeRemaining: _session.TimeRemaining);
    }

    private static string GetStatusEmoji(string status)
    {
        return status switch
        {
            nameof(LockStatus.Active) => "🟢",
            nameof(LockStatus.Blocked) => "🔴",
            nameof(LockStatus.ForceBlocked) => "🔒",
            _ => "⚪"
        };
    }
}
