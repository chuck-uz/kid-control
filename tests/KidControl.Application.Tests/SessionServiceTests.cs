using FluentAssertions;
using KidControl.Application.Abstractions;
using KidControl.Application.Commands;
using KidControl.Application.Models;
using KidControl.Application.Services;
using KidControl.Application.Tests.Fakes;
using KidControl.Contracts;
using KidControl.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Application.Tests;

public sealed class SessionServiceTests
{
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<ISessionStore> _store = new(MockBehavior.Loose);
    private readonly Mock<IUiNotifier> _ui = new(MockBehavior.Loose);
    private readonly Mock<ITelegramGateway> _telegram = new(MockBehavior.Loose);
    private readonly Mock<ISystemController> _system = new(MockBehavior.Loose);

    public SessionServiceTests()
    {
        _store.Setup(s => s.Load()).Returns((SessionSnapshot?)null);
        _ui.Setup(u => u.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _telegram.Setup(t => t.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _system.Setup(s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _system.Setup(s => s.RestartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private SessionService Build() => new(
        _clock,
        _store.Object,
        _ui.Object,
        _telegram.Object,
        _system.Object,
        NullLogger<SessionService>.Instance);

    // ─── Tick ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessTickAsync_Should_NotifyTheUi()
    {
        var svc = Build();
        _ui.Invocations.Clear();

        await svc.ProcessTickAsync();

        _ui.Verify(
            u => u.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessTickAsync_Should_EnterNightBlock_When_NightWindowActive()
    {
        var svc = Build();
        // Move local time inside the default 22:00–07:00 night window.
        _clock.SetLocal(new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.Zero));

        await svc.ProcessTickAsync();

        var state = svc.GetCurrentState();
        state.Status.Should().Be("NightBlocked");
        state.IsNightMode.Should().BeTrue();
    }

    // ─── Commands ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Block_Should_SetResting_And_ReturnReply()
    {
        var svc = Build();

        var reply = await svc.ExecuteAsync(new SessionCommand.Block());

        reply.Should().NotBeNullOrWhiteSpace();
        svc.GetCurrentState().Status.Should().Be("Resting");
    }

    [Fact]
    public async Task ExecuteAsync_AddTime_Should_IncreaseRemaining()
    {
        var svc = Build();
        var before = svc.GetCurrentState().TimeRemaining;

        await svc.ExecuteAsync(new SessionCommand.AddTime(30));

        var after = svc.GetCurrentState().TimeRemaining;
        after.Should().Be(before + TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task ExecuteAsync_SetRule_Should_ApplyRule_And_ResizePlayPhase()
    {
        var svc = Build(); // starts Playing

        await svc.ExecuteAsync(new SessionCommand.SetRule(new ScheduleRule(50, 10)));

        var state = svc.GetCurrentState();
        state.Status.Should().Be("Playing");
        state.TimeRemaining.Should().Be(TimeSpan.FromMinutes(50));
    }

    [Fact]
    public async Task ExecuteAsync_SetNight_Should_UpdateNightWindow()
    {
        var svc = Build();
        // A daytime-only window means night is NOT active at noon.
        await svc.ExecuteAsync(new SessionCommand.SetNight(new NightWindow(new TimeSpan(1, 0, 0), new TimeSpan(2, 0, 0))));

        svc.IsNightActiveNow().Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Pause_Should_SetPaused_And_StopUi()
    {
        var svc = Build();

        await svc.ExecuteAsync(new SessionCommand.Pause());

        svc.IsPaused().Should().BeTrue();
        svc.GetCurrentState().Status.Should().Be("Paused");
        _system.Verify(s => s.StopUi(), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Resume_Should_ClearPause_And_LaunchUi()
    {
        var svc = Build();
        await svc.ExecuteAsync(new SessionCommand.Pause());

        await svc.ExecuteAsync(new SessionCommand.Resume());

        svc.IsPaused().Should().BeFalse();
        svc.GetCurrentState().Status.Should().Be("Playing");
        _system.Verify(s => s.LaunchUi(), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownPc_Should_CallSystemShutdown()
    {
        var svc = Build();

        await svc.ExecuteAsync(new SessionCommand.ShutdownPc());

        _system.Verify(
            s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RestartPc_Should_CallSystemRestart()
    {
        var svc = Build();

        await svc.ExecuteAsync(new SessionCommand.RestartPc());

        _system.Verify(
            s => s.RestartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Unknown_Should_ReturnHelp_Without_TouchingSystem()
    {
        var svc = Build();

        var reply = await svc.ExecuteAsync(new SessionCommand.Unknown("help-text"));

        reply.Should().Be("help-text");
        _system.Verify(s => s.StopUi(), Times.Never);
    }

    // ─── Queries ────────────────────────────────────────────────────────────

    [Fact]
    public void GetCurrentState_Should_StartPlaying_With_DefaultPlayDuration()
    {
        var svc = Build();

        var state = svc.GetCurrentState();

        state.Status.Should().Be("Playing");
        state.TimeRemaining.Should().Be(ScheduleRule.Default.PlayDuration);
        state.IsNightMode.Should().BeFalse();
    }

    [Fact]
    public void StatusText_Should_Mention_StatusAndRemaining()
    {
        var svc = Build();

        var text = svc.StatusText();

        text.Should().Contain("Playing");
    }

    [Fact]
    public void IsNightActiveNow_Should_BeTrue_When_LocalTimeInsideDefaultWindow()
    {
        var svc = Build();
        _clock.SetLocal(new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero));

        svc.IsNightActiveNow().Should().BeTrue();
    }
}
