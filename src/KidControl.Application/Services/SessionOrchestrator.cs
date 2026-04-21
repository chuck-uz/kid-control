using System.Diagnostics;
using System.Security.Cryptography;
using KidControl.Application.Interfaces;
using KidControl.Application.Models;
using KidControl.Contracts;
using KidControl.Domain.Entities;
using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Application.Services;

public sealed class SessionOrchestrator
{
    private const int DefaultPlayMinutes = 40;
    private const int DefaultRestMinutes = 20;
    private readonly object _sync = new();
    private readonly HashSet<long> _awaitingCustomRuleInput = [];
    private readonly HashSet<long> _awaitingNightModeInput = [];
    private readonly ComputerSession _session;
    private readonly IUiNotifier _uiNotifier;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly ISessionStateRepository _sessionStateRepository;
    private readonly IHostApplicationLifetime? _hostApplicationLifetime;
    private readonly ILogger<SessionOrchestrator> _logger;
    private string? _pendingOtp;
    private DateTimeOffset _otpExpiresAt;
    private DateTimeOffset _lastTickAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTickLogAt = DateTimeOffset.MinValue;
    private TimeSpan _nightModeStart = TimeSpan.FromHours(22);
    private TimeSpan _nightModeEnd = TimeSpan.FromHours(7);
    private DateTimeOffset _nightModeBypassUntil = DateTimeOffset.MinValue;
    private LockStatus _statusBeforeNightBlock = LockStatus.Active;
    private TimeSpan _remainingBeforeNightBlock = TimeSpan.Zero;
    private bool _hasNightBlockSnapshot;
    private DateTimeOffset _lastNightUsageAlert = DateTimeOffset.MinValue;

    public SessionOrchestrator(
        IUiNotifier uiNotifier,
        ITelegramNotifier telegramNotifier,
        ISessionStateRepository sessionStateRepository,
        ILogger<SessionOrchestrator> logger,
        IHostApplicationLifetime? hostApplicationLifetime = null)
    {
        _uiNotifier = uiNotifier ?? throw new ArgumentNullException(nameof(uiNotifier));
        _telegramNotifier = telegramNotifier ?? throw new ArgumentNullException(nameof(telegramNotifier));
        _sessionStateRepository = sessionStateRepository ?? throw new ArgumentNullException(nameof(sessionStateRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostApplicationLifetime = hostApplicationLifetime;

        _session = new ComputerSession();
        _session.SetRule(new ScheduleRule(playMinutes: DefaultPlayMinutes, restMinutes: DefaultRestMinutes));
        _session.AddTime(TimeSpan.FromMinutes(DefaultPlayMinutes));
        RestorePersistedState();
        SaveStateSnapshot();
        // Ensure the very first worker cycle immediately decrements when status is Active.
        _lastTickAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1);
    }

    public void SetNightModeWindow(TimeSpan start, TimeSpan end)
    {
        lock (_sync)
        {
            _nightModeStart = start;
            _nightModeEnd = end;
        }

        SaveStateSnapshot();
    }

    public async Task ProcessTickAsync()
    {
        SessionStateDto state;
        LockStatus previousStatus;
        LockStatus newStatus;
        var isNightMode = false;
        var currentPlayMinutes = DefaultPlayMinutes;
        var currentRestMinutes = DefaultRestMinutes;

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var nowLocal = DateTimeOffset.Now;
            previousStatus = _session.CurrentStatus;
            isNightMode = IsNightMode(nowLocal.TimeOfDay);

            if (isNightMode && !IsNightBypassActive(nowLocal))
            {
                EnterNightBlockIfNeeded();
                _lastTickAt = now;
                newStatus = _session.CurrentStatus;
                state = ToDto(isNightMode);
                currentPlayMinutes = _session.CurrentRule?.PlayMinutes ?? DefaultPlayMinutes;
                currentRestMinutes = _session.CurrentRule?.RestMinutes ?? DefaultRestMinutes;
            }
            else
            {
                ExitNightBlockIfNeeded();

                if (_session.CurrentStatus == LockStatus.Paused)
                {
                    _lastTickAt = now;
                    newStatus = previousStatus;
                    state = ToDto(isNightMode);
                }
                else
                {
                    var elapsed = now - _lastTickAt;
                    if (elapsed < TimeSpan.FromSeconds(1))
                    {
                        elapsed = TimeSpan.FromSeconds(1);
                    }
                    else if (elapsed > TimeSpan.FromMinutes(2))
                    {
                        // Prevent a giant single jump; regular ticks will continue right after.
                        elapsed = TimeSpan.FromMinutes(2);
                    }

                    _session.Tick(elapsed);
                    _lastTickAt = now;
                    newStatus = _session.CurrentStatus;
                    state = ToDto(isNightMode);
                }
            }

            currentPlayMinutes = _session.CurrentRule?.PlayMinutes ?? DefaultPlayMinutes;
            currentRestMinutes = _session.CurrentRule?.RestMinutes ?? DefaultRestMinutes;
        }

        await _uiNotifier.NotifyStateChangedAsync(state).ConfigureAwait(false);
        SaveStateSnapshot();

        var logNow = DateTimeOffset.UtcNow;
        if (logNow - _lastTickLogAt >= TimeSpan.FromSeconds(5))
        {
            _lastTickLogAt = logNow;
            _logger.LogInformation(
                "Timer Tick: Status={Status}, Remaining={Remaining}, Rule={Play}/{Rest}",
                state.Status,
                state.TimeRemaining,
                currentPlayMinutes,
                currentRestMinutes);
        }

        if (previousStatus == LockStatus.Active && newStatus == LockStatus.Blocked)
        {
            try
            {
                var notifyTask = _telegramNotifier.BroadcastAsync("⚡ Время игры вышло. Компьютер перешел в режим блокировки.");
                var completed = await Task.WhenAny(notifyTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (completed == notifyTask)
                {
                    await notifyTask.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send block transition notification to Telegram.");
            }
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

        if (await TryHandleNightModeInputAsync(chatId, commandText).ConfigureAwait(false))
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
        SaveStateSnapshot();
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
        SaveStateSnapshot();
        return $"✅ Новое правило установлено: {(int)workTime.TotalMinutes} мин работы и {(int)restTime.TotalMinutes} мин отдыха. Таймер на ПК обновлен.";
    }

    public async Task<string> ResetSessionTimeAsync()
    {
        SessionStateDto state;
        lock (_sync)
        {
            var rule = _session.CurrentRule ?? new ScheduleRule(playMinutes: DefaultPlayMinutes, restMinutes: DefaultRestMinutes);
            _session.SetRuleAndResetPhase(rule);
            state = ToDto();
        }

        await _uiNotifier.NotifyStateChangedAsync(state).ConfigureAwait(false);
        SaveStateSnapshot();
        return "Таймер сброшен на текущий игровой интервал.";
    }

    public void BeginCustomRuleInput(long chatId)
    {
        lock (_sync)
        {
            _awaitingCustomRuleInput.Add(chatId);
        }
    }

    public void BeginNightModeInput(long chatId)
    {
        lock (_sync)
        {
            _awaitingNightModeInput.Add(chatId);
        }
    }

    public SessionStateDto GetCurrentState()
    {
        lock (_sync)
        {
            return ToDto(IsNightMode(DateTimeOffset.Now.TimeOfDay));
        }
    }

    /// <summary>Pushes current session state to UiHost (e.g. right after the watchdog starts the UI process).</summary>
    public Task NotifyCurrentStateToUiAsync()
    {
        return _uiNotifier.NotifyStateChangedAsync(GetCurrentState());
    }

    public bool IsNightModeActiveNow()
    {
        lock (_sync)
        {
            return IsNightMode(DateTimeOffset.Now.TimeOfDay);
        }
    }

    public async Task NotifyNightUsageAttemptAsync()
    {
        var shouldSend = false;
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastNightUsageAlert >= TimeSpan.FromMinutes(5))
            {
                _lastNightUsageAlert = now;
                shouldSend = true;
            }
        }

        if (shouldSend)
        {
            await _telegramNotifier
                .BroadcastAsync("⚠️ Внимание: Была попытка использования ПК в ночное время")
                .ConfigureAwait(false);
        }
    }

    public async Task<string> UpdateNightModeWindow(TimeSpan start, TimeSpan end)
    {
        lock (_sync)
        {
            _nightModeStart = start;
            _nightModeEnd = end;
            _nightModeBypassUntil = DateTimeOffset.MinValue;

            if (IsNightMode(DateTimeOffset.Now.TimeOfDay))
            {
                EnterNightBlockIfNeeded();
            }
            else
            {
                ExitNightBlockIfNeeded();
            }
        }

        _logger.LogInformation("Night mode window updated. Start={Start}, End={End}", start, end);
        await _uiNotifier.NotifyStateChangedAsync(GetCurrentState()).ConfigureAwait(false);
        SaveStateSnapshot();
        return $"🌙 Ночной интервал обновлен: {start:hh\\:mm}-{end:hh\\:mm}.";
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

    public bool IsPaused()
    {
        lock (_sync)
        {
            return _session.CurrentStatus == LockStatus.Paused;
        }
    }

    public async Task<string> PauseSystem()
    {
        lock (_sync)
        {
            _session.SetPaused();
        }

        EnsureUiStopped();
        SaveStateSnapshot();
        await _uiNotifier.NotifyStateChangedAsync(GetCurrentState()).ConfigureAwait(false);
        return "Система на паузе. Контроль отключен, но я остаюсь на связи.";
    }

    public async Task<string> ResumeSystem()
    {
        lock (_sync)
        {
            if (_session.CurrentStatus == LockStatus.Paused)
            {
                _session.ForceUnblock();
            }
            _lastTickAt = DateTimeOffset.UtcNow;
        }

        TryLaunchUiHostAfterResume();
        SaveStateSnapshot();
        await _uiNotifier.NotifyStateChangedAsync(GetCurrentState()).ConfigureAwait(false);
        return "Система включена. Контроль возобновлен.";
    }

    public string GetCompactStatusText()
    {
        lock (_sync)
        {
            return _session.CurrentStatus switch
            {
                LockStatus.Active => "🟢 Активно",
                LockStatus.Blocked => "🔴 Блок",
                LockStatus.ForceBlocked => "🔒 Принудительный блок",
                LockStatus.Paused => "⏸️ Пауза",
                LockStatus.NightBlock => "🌙 Ночной блок",
                _ => "⚪ Неизвестно"
            };
        }
    }

    public string GetStatusDetailsText()
    {
        lock (_sync)
        {
            var status = _session.CurrentStatus switch
            {
                LockStatus.Active => "🟢 Активно",
                LockStatus.Blocked => "🔴 Блок",
                LockStatus.ForceBlocked => "🔴 Блок",
                LockStatus.Paused => "⏸️ Пауза",
                LockStatus.NightBlock => "🔴 Блок",
                _ => "⚪ Неизвестно"
            };

            return $"Статус: {status}\n⏳ Осталось: {_session.TimeRemaining:hh\\:mm\\:ss}";
        }
    }

    public async Task<string> AddTimePresetAsync(int minutes)
    {
        if (minutes <= 0)
        {
            return "Некорректное значение времени.";
        }

        SessionStateDto state;
        lock (_sync)
        {
            var nowLocal = DateTimeOffset.Now;
            if (IsNightMode(nowLocal.TimeOfDay))
            {
                ActivateNightBypass(nowLocal);
                ExitNightBlockIfNeeded();
            }
            _session.AddTime(TimeSpan.FromMinutes(minutes));
            state = ToDto();
        }

        await _uiNotifier.NotifyStateChangedAsync(state).ConfigureAwait(false);
        SaveStateSnapshot();
        return $"Добавлено {minutes} минут. Новый остаток: {state.TimeRemaining:hh\\:mm\\:ss}";
    }

    public void EnsureUiStopped()
    {
        foreach (var process in Process.GetProcessesByName("KidControl.UiHost"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort, ignore per-process failures.
            }
        }
    }

    public async Task HardKill()
    {
        EnsureUiStopped();
        SaveStateSnapshot();

        // Do not block kill if Telegram API hangs.
        try
        {
            var broadcastTask = _telegramNotifier.BroadcastAsync("🛑 Служба завершает процесс по команде администратора.");
            var completed = await Task.WhenAny(broadcastTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (completed == broadcastTask)
            {
                await broadcastTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HardKill broadcast failed.");
        }

        _hostApplicationLifetime?.StopApplication();

        // Fallback in case host stop is blocked by a hosted service.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }

    public async Task ShutdownPc()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 10 /f",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(3000);
            _logger.LogInformation("Shutdown command executed. ExitCode={ExitCode}", process?.ExitCode);
            await _telegramNotifier.BroadcastAsync("🔌 Команда выключения ПК отправлена. Отключение через 10 секунд.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute shutdown command.");
            await _telegramNotifier.BroadcastAsync("❌ Не удалось выполнить выключение ПК.").ConfigureAwait(false);
        }
    }

    public async Task RestartPc()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 10 /f",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(3000);
            _logger.LogInformation("Restart command executed. ExitCode={ExitCode}", process?.ExitCode);
            await _telegramNotifier.BroadcastAsync("🔄 Команда перезагрузки ПК отправлена. Перезагрузка через 10 секунд.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute restart command.");
            await _telegramNotifier.BroadcastAsync("❌ Не удалось выполнить перезагрузку ПК.").ConfigureAwait(false);
        }
    }

    private string HandleBlock()
    {
        _session.BlockForRest();
        return "Компьютер заблокирован. Запущен таймер перерыва.";
    }

    private string HandleUnblock()
    {
        var nowLocal = DateTimeOffset.Now;
        if (IsNightMode(nowLocal.TimeOfDay))
        {
            ActivateNightBypass(nowLocal);
        }
        ExitNightBlockIfNeeded();
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

        var nowLocal = DateTimeOffset.Now;
        if (IsNightMode(nowLocal.TimeOfDay))
        {
            ActivateNightBypass(nowLocal);
            ExitNightBlockIfNeeded();
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

    public async Task<bool> TryHandleNightModeInputAsync(long chatId, string input)
    {
        bool isAwaiting;
        lock (_sync)
        {
            isAwaiting = _awaitingNightModeInput.Contains(chatId);
        }

        if (!isAwaiting)
        {
            return false;
        }

        var parts = input.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !TimeSpan.TryParseExact(parts[0], @"hh\:mm", null, out var start) ||
            !TimeSpan.TryParseExact(parts[1], @"hh\:mm", null, out var end))
        {
            await _telegramNotifier.SendReplyAsync(chatId, "❌ Неверный формат. Введите интервал как 21:30-08:00.")
                .ConfigureAwait(false);
            return true;
        }

        lock (_sync)
        {
            _awaitingNightModeInput.Remove(chatId);
        }

        var confirmation = await UpdateNightModeWindow(start, end).ConfigureAwait(false);
        await _telegramNotifier.SendReplyAsync(chatId, confirmation).ConfigureAwait(false);
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
        var nightLabel = state.IsNightMode ? "активно" : "не активно";
        return $"Статус: {GetStatusEmoji(state.Status)} {state.Status}. Осталось: {state.TimeRemaining:c}. Ночь ({_nightModeStart:hh\\:mm}-{_nightModeEnd:hh\\:mm}): {nightLabel}.";
    }

    private SessionStateDto ToDto(bool? isNightModeOverride = null)
    {
        var isNightMode = isNightModeOverride ?? IsNightMode(DateTimeOffset.Now.TimeOfDay);
        return new SessionStateDto(
            Status: _session.CurrentStatus.ToString(),
            TimeRemaining: _session.TimeRemaining,
            IsNightMode: isNightMode);
    }

    private static string GetStatusEmoji(string status)
    {
        return status switch
        {
            nameof(LockStatus.Active) => "🟢",
            nameof(LockStatus.Blocked) => "🔴",
            nameof(LockStatus.ForceBlocked) => "🔒",
            nameof(LockStatus.NightBlock) => "🌙",
            nameof(LockStatus.Paused) => "⏸️",
            _ => "⚪"
        };
    }

    private bool IsNightMode(TimeSpan now)
    {
        if (_nightModeStart == _nightModeEnd)
        {
            return true;
        }

        if (_nightModeStart < _nightModeEnd)
        {
            return now >= _nightModeStart && now < _nightModeEnd;
        }

        return now >= _nightModeStart || now < _nightModeEnd;
    }

    private void RestorePersistedState()
    {
        try
        {
            var persisted = _sessionStateRepository.Load();
            if (persisted is null)
            {
                return;
            }

            var delta = DateTimeOffset.Now - persisted.LastUpdateTimestamp;
            if (delta < TimeSpan.Zero)
            {
                delta = TimeSpan.Zero;
            }

            var playMinutes = persisted.PlayMinutes > 0 ? persisted.PlayMinutes : DefaultPlayMinutes;
            var restMinutes = persisted.RestMinutes > 0 ? persisted.RestMinutes : DefaultRestMinutes;
            _nightModeStart = persisted.NightModeStart == default ? TimeSpan.FromHours(22) : persisted.NightModeStart;
            _nightModeEnd = persisted.NightModeEnd == default ? TimeSpan.FromHours(7) : persisted.NightModeEnd;
            _session.SetRule(new ScheduleRule(playMinutes, restMinutes));
            _session.RestoreState(persisted.CurrentStatus, persisted.TimeRemaining);
            if (delta > TimeSpan.Zero)
            {
                _session.Tick(delta);
            }

            _lastTickAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore persisted session state.");
        }
    }

    private void SaveStateSnapshot()
    {
        try
        {
            SessionPersistenceState snapshot;
            lock (_sync)
            {
                var currentRule = _session.CurrentRule ?? new ScheduleRule(DefaultPlayMinutes, DefaultRestMinutes);
                snapshot = new SessionPersistenceState
                {
                    TimeRemaining = _session.TimeRemaining,
                    CurrentStatus = _session.CurrentStatus,
                    LastUpdateTimestamp = DateTimeOffset.Now,
                    PlayMinutes = currentRule.PlayMinutes,
                    RestMinutes = currentRule.RestMinutes,
                    NightModeStart = _nightModeStart,
                    NightModeEnd = _nightModeEnd
                };
            }

            _sessionStateRepository.Save(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist current session snapshot.");
        }
    }

    private void TryLaunchUiHostAfterResume()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Run /TN \"KidControl.UiHost.Launch\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            process?.WaitForExit(3000);
            _logger.LogInformation("Resume UI launch attempt via scheduler finished. ExitCode={ExitCode}", process?.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger UI launch after resume.");
        }
    }

    private void EnterNightBlockIfNeeded()
    {
        if (_session.CurrentStatus == LockStatus.NightBlock)
        {
            return;
        }

        _statusBeforeNightBlock = _session.CurrentStatus;
        _remainingBeforeNightBlock = _session.TimeRemaining;
        _hasNightBlockSnapshot = true;
        _session.SetNightBlock();
    }

    private void ExitNightBlockIfNeeded()
    {
        if (_session.CurrentStatus != LockStatus.NightBlock)
        {
            return;
        }

        if (_hasNightBlockSnapshot)
        {
            _session.RestoreState(_statusBeforeNightBlock, _remainingBeforeNightBlock);
            _hasNightBlockSnapshot = false;
            return;
        }

        _session.ForceUnblock();
    }

    private bool IsNightBypassActive(DateTimeOffset nowLocal)
    {
        return nowLocal < _nightModeBypassUntil;
    }

    private void ActivateNightBypass(DateTimeOffset nowLocal)
    {
        _nightModeBypassUntil = GetCurrentNightWindowEnd(nowLocal);
    }

    private DateTimeOffset GetCurrentNightWindowEnd(DateTimeOffset nowLocal)
    {
        if (_nightModeStart == _nightModeEnd)
        {
            return new DateTimeOffset(nowLocal.Date.AddDays(1).Add(_nightModeEnd), nowLocal.Offset);
        }

        if (_nightModeStart < _nightModeEnd)
        {
            return new DateTimeOffset(nowLocal.Date.Add(_nightModeEnd), nowLocal.Offset);
        }

        var endDate = nowLocal.TimeOfDay < _nightModeEnd
            ? nowLocal.Date
            : nowLocal.Date.AddDays(1);
        return new DateTimeOffset(endDate.Add(_nightModeEnd), nowLocal.Offset);
    }
}
