using KidControl.Application.Abstractions;
using KidControl.Application.Commands;
using KidControl.Application.Models;
using KidControl.Contracts;
using KidControl.Domain.Entities;
using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace KidControl.Application.Services;

/// <summary>
/// Coordinates the domain <see cref="Session"/> with persistence, UI push, and
/// admin notifications. Compared with the original 979-line orchestrator this is
/// intentionally lean: night-block bookkeeping now lives in the domain, OTP is a
/// separate service, OS side effects go through <see cref="ISystemController"/>,
/// command parsing is a pure function, and time comes from <see cref="IClock"/>.
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan MaxTickJump = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NightAlertThrottle = TimeSpan.FromMinutes(5);

    /// <summary>Grace shown on-screen after the night block engages, before the PC is shut down.</summary>
    private static readonly TimeSpan NightShutdownGrace = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private Session _session = new();
    private readonly IClock _clock;
    private readonly ISessionStore _store;
    private readonly IUiNotifier _ui;
    private readonly ITelegramGateway _telegram;
    private readonly ISystemController _system;
    private readonly ILogger<SessionService> _logger;

    private NightWindow _night = NightWindow.Default;
    private DateTimeOffset _nightBypassUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTickAt;
    private DateTimeOffset _lastNightAlert = DateTimeOffset.MinValue;

    // Night auto-shutdown: when the night block is active we count down from
    // NightShutdownGrace and then power the PC off. Reset whenever we leave the block
    // (so it restarts on the next boot / re-entry, and is cancelled by an admin bypass).
    private DateTimeOffset? _nightShutdownAt;
    private bool _nightShutdownFired;

    public SessionService(
        IClock clock,
        ISessionStore store,
        IUiNotifier ui,
        ITelegramGateway telegram,
        ISystemController system,
        ILogger<SessionService> logger,
        SessionDefaults? defaults = null)
    {
        _clock = clock;
        _store = store;
        _ui = ui;
        _telegram = telegram;
        _system = system;
        _logger = logger;

        // Seed fresh-session defaults (e.g. the night window configured in appsettings).
        // Restore() overrides these when a persisted snapshot exists.
        var seed = defaults ?? SessionDefaults.Standard;
        _night = seed.Night;
        _session.ApplyRuleResettingPhase(seed.Rule);

        _lastTickAt = _clock.UtcNow;
        Restore();
        Persist();
    }

    // ─── Timer loop ─────────────────────────────────────────────────────────

    public async Task ProcessTickAsync(CancellationToken ct = default)
    {
        SessionStateDto state;
        SessionStatus before, after;
        var shutdownNow = false;

        lock (_sync)
        {
            var now = _clock.UtcNow;
            var localNow = _clock.LocalNow;
            before = _session.Status;

            if (IsNightActive(localNow) && !IsBypassActive(now))
            {
                _session.EnterNight();

                // Arm (once) the on-screen grace countdown, then power off when it elapses.
                _nightShutdownAt ??= now + NightShutdownGrace;
                if (!_nightShutdownFired && now >= _nightShutdownAt.Value)
                {
                    _nightShutdownFired = true;
                    shutdownNow = true;
                }
            }
            else
            {
                _session.ExitNight();
                _nightShutdownAt = null;
                _nightShutdownFired = false;
                if (_session.Status != SessionStatus.Paused)
                {
                    var elapsed = ClampElapsed(now - _lastTickAt);
                    _session.Tick(elapsed);
                }
            }

            _lastTickAt = now;
            after = _session.Status;
            state = Snapshot(IsNightActive(localNow));
        }

        await _ui.NotifyStateChangedAsync(state, ct).ConfigureAwait(false);
        Persist();

        if (before == SessionStatus.Playing && after == SessionStatus.Resting)
        {
            await NotifySafeAsync("⚡ Время игры вышло. Компьютер перешёл в режим перерыва.", ct).ConfigureAwait(false);
        }

        if (shutdownNow)
        {
            await NotifySafeAsync("🌙 Ночное время: выключаю компьютер.", ct).ConfigureAwait(false);
            try
            {
                await _system.ShutdownAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Night auto-shutdown failed.");
            }
        }
    }

    /// <summary>Seconds left on the night grace countdown, or -1 when it is not counting.</summary>
    private int NightShutdownSeconds()
    {
        if (_session.Status != SessionStatus.NightBlocked || _nightShutdownAt is not { } at)
        {
            return -1;
        }

        var remaining = (at - _clock.UtcNow).TotalSeconds;
        return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining);
    }

    private static TimeSpan ClampElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return elapsed > MaxTickJump ? MaxTickJump : elapsed;
    }

    // ─── Command execution ──────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(SessionCommand command, CancellationToken ct = default)
    {
        string reply;
        switch (command)
        {
            case SessionCommand.Status:
                reply = StatusText();
                break;
            case SessionCommand.Block:
                lock (_sync) { _session.StartRest(); }
                reply = "Компьютер заблокирован. Запущен таймер перерыва.";
                break;
            case SessionCommand.Unblock:
                lock (_sync) { BypassNightIfNeeded(); _session.ExitNight(); _session.ReleaseBlock(); }
                reply = "Компьютер разблокирован.";
                break;
            case SessionCommand.AddTime add:
                lock (_sync) { BypassNightIfNeeded(); _session.ExitNight(); _session.AddTime(TimeSpan.FromMinutes(add.Minutes)); }
                reply = $"Добавлено {add.Minutes} мин. Остаток: {Remaining():hh\\:mm\\:ss}.";
                break;
            case SessionCommand.SetRule rule:
                lock (_sync) { _session.ApplyRuleResettingPhase(rule.Rule); }
                reply = $"✅ Правило: {rule.Rule.PlayMinutes}/{rule.Rule.RestMinutes}. Таймер обновлён.";
                break;
            case SessionCommand.SetNight night:
                lock (_sync) { _night = night.Window; _nightBypassUntil = DateTimeOffset.MinValue; }
                reply = $"🌙 Ночной интервал: {night.Window}.";
                break;
            case SessionCommand.ResetTimer:
                lock (_sync) { _session.ResetToPlayStart(); }
                reply = "Таймер сброшен на игровой интервал.";
                break;
            case SessionCommand.SetIntervals si:
                lock (_sync) { if (si.Enabled) _session.EnableIntervals(); else _session.DisableIntervals(); }
                reply = si.Enabled
                    ? "✅ Интервалы включены. Действует режим игра/отдых."
                    : "♾️ Интервалы отключены. Без ограничений по времени (ночной режим по-прежнему действует).";
                break;
            case SessionCommand.Pause:
                lock (_sync) { _session.Pause(); }
                _system.StopUi();
                reply = "Система на паузе. Контроль отключён.";
                break;
            case SessionCommand.Resume:
                lock (_sync) { _session.Resume(); _lastTickAt = _clock.UtcNow; }
                _system.LaunchUi();
                reply = "Система включена. Контроль возобновлён.";
                break;
            case SessionCommand.ShutdownPc:
                await _system.ShutdownAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                reply = "🔌 Команда выключения отправлена (через 10 сек).";
                break;
            case SessionCommand.RestartPc:
                await _system.RestartAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                reply = "🔄 Команда перезагрузки отправлена (через 10 сек).";
                break;
            case SessionCommand.Unknown u:
                return u.Help;
            default:
                return "Неизвестная команда.";
        }

        await PushStateAsync(ct).ConfigureAwait(false);
        Persist();
        return reply;
    }

    // ─── Queries ────────────────────────────────────────────────────────────

    public SessionStateDto GetCurrentState()
    {
        lock (_sync)
        {
            return Snapshot(IsNightActive(_clock.LocalNow));
        }
    }

    public bool IsPaused()
    {
        lock (_sync) { return _session.Status == SessionStatus.Paused; }
    }

    public bool IsNightActiveNow()
    {
        lock (_sync) { return IsNightActive(_clock.LocalNow); }
    }

    public Task NotifyCurrentStateToUiAsync(CancellationToken ct = default)
        => _ui.NotifyStateChangedAsync(GetCurrentState(), ct);

    public async Task NotifyNightUsageAttemptAsync(CancellationToken ct = default)
    {
        bool send;
        lock (_sync)
        {
            var now = _clock.UtcNow;
            send = now - _lastNightAlert >= NightAlertThrottle;
            if (send)
            {
                _lastNightAlert = now;
            }
        }

        if (send)
        {
            await NotifySafeAsync("⚠️ Попытка использования ПК в ночное время.", ct).ConfigureAwait(false);
        }
    }

    public string GetNightWindowText()
    {
        lock (_sync) { return _night.ToString(); }
    }

    public string StatusText()
    {
        lock (_sync)
        {
            var night = IsNightActive(_clock.LocalNow) ? "активно" : "не активно";
            return $"{Emoji(_session.Status)} {_session.Status}. Осталось: {_session.TimeRemaining:hh\\:mm\\:ss}. " +
                   $"Ночь ({_night}): {night}.";
        }
    }

    // ─── Internals ──────────────────────────────────────────────────────────

    private async Task PushStateAsync(CancellationToken ct)
        => await _ui.NotifyStateChangedAsync(GetCurrentState(), ct).ConfigureAwait(false);

    private TimeSpan Remaining()
    {
        lock (_sync) { return _session.TimeRemaining; }
    }

    private bool IsNightActive(DateTimeOffset local) => _night.Contains(local.TimeOfDay);

    private bool IsBypassActive(DateTimeOffset now) => now < _nightBypassUntil;

    private void BypassNightIfNeeded()
    {
        var local = _clock.LocalNow;
        if (IsNightActive(local))
        {
            _nightBypassUntil = _night.NextEnd(local);
        }
    }

    private SessionStateDto Snapshot(bool isNight) =>
        new(_session.Status.ToString(), _session.TimeRemaining, isNight, !_session.IntervalsEnabled, NightShutdownSeconds());

    private static string Emoji(SessionStatus status) => status switch
    {
        SessionStatus.Playing => "🟢",
        SessionStatus.Resting => "🔴",
        SessionStatus.ForceBlocked => "🔒",
        SessionStatus.Paused => "⏸️",
        SessionStatus.NightBlocked => "🌙",
        _ => "⚪"
    };

    private async Task NotifySafeAsync(string message, CancellationToken ct)
    {
        try
        {
            var task = _telegram.BroadcastAsync(message, ct);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
            if (completed == task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram notify failed (non-fatal).");
        }
    }

    private void Restore()
    {
        try
        {
            var snapshot = _store.Load();
            if (snapshot is null)
            {
                return;
            }

            var rule = new ScheduleRule(
                snapshot.PlayMinutes > 0 ? snapshot.PlayMinutes : ScheduleRule.Default.PlayMinutes,
                snapshot.RestMinutes > 0 ? snapshot.RestMinutes : ScheduleRule.Default.RestMinutes);
            _night = new NightWindow(snapshot.NightStart, snapshot.NightEnd);

            lock (_sync)
            {
                _session = Session.Restore(snapshot.Status, snapshot.TimeRemaining, rule, snapshot.IntervalsEnabled);

                // Account for time elapsed while the service was down.
                var delta = _clock.LocalNow - snapshot.LastUpdated;
                if (delta > TimeSpan.Zero)
                {
                    _session.Tick(delta);
                }

                _lastTickAt = _clock.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore persisted session.");
        }
    }

    private void Persist()
    {
        try
        {
            SessionSnapshot snapshot;
            lock (_sync)
            {
                snapshot = new SessionSnapshot
                {
                    TimeRemaining = _session.TimeRemaining,
                    Status = _session.Status,
                    LastUpdated = _clock.LocalNow,
                    PlayMinutes = _session.Rule.PlayMinutes,
                    RestMinutes = _session.Rule.RestMinutes,
                    NightStart = _night.Start,
                    NightEnd = _night.End,
                    IntervalsEnabled = _session.IntervalsEnabled
                };
            }

            _store.Save(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist session snapshot.");
        }
    }
}
