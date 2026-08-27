using FluentAssertions;
using KidControl.Application.Abstractions;
using KidControl.Application.Services;
using KidControl.Contracts;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public class FleetDesiredApplierTests
{
    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public DateTimeOffset LocalNow { get; set; } = now;
    }

    private static (SessionService session, FleetDesiredApplier applier, TestClock clock) Build(int hourLocal = 12)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, hourLocal, 0, 0, TimeSpan.Zero));
        var ui = new Mock<IUiNotifier>();
        ui.Setup(x => x.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var tg = new Mock<ITelegramGateway>();
        tg.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var system = new Mock<ISystemController>();
        var store = new Mock<ISessionStore>();
        var session = new SessionService(clock, store.Object, ui.Object, tg.Object, system.Object,
            NullLogger<SessionService>.Instance);
        var applier = new FleetDesiredApplier(session, NullLogger<FleetDesiredApplier>.Instance);
        return (session, applier, clock);
    }

    private static DesiredStateDto Desired(int version = 1, bool paused = false, bool forceBlocked = false,
        DateTimeOffset? bypass = null) => new()
    {
        Version = version, Paused = paused, ForceBlocked = forceBlocked, NightBypassUntil = bypass
    };

    [Fact]
    public async Task Force_block_blocks_then_releases()
    {
        var (session, applier, _) = Build();

        await applier.ApplyAsync(Desired(1, forceBlocked: true));
        session.GetCurrentState().Status.Should().Be("ForceBlocked");

        await applier.ApplyAsync(Desired(2, forceBlocked: false));
        session.GetCurrentState().Status.Should().Be("Playing");
    }

    [Fact]
    public async Task Pause_takes_precedence_over_force_block()
    {
        var (session, applier, _) = Build();

        await applier.ApplyAsync(Desired(1, paused: true, forceBlocked: true));

        session.IsPaused().Should().BeTrue();
        session.GetCurrentState().Status.Should().Be("Paused"); // not ForceBlocked
    }

    [Fact]
    public async Task Applying_same_desired_twice_is_idempotent()
    {
        var (session, applier, _) = Build();

        await applier.ApplyAsync(Desired(1, forceBlocked: true));
        await applier.ApplyAsync(Desired(1, forceBlocked: true));

        session.GetCurrentState().Status.Should().Be("ForceBlocked");
    }

    [Fact]
    public async Task Night_bypass_suspends_the_night_block()
    {
        // 23:00 is inside the default 22:00–07:00 night window.
        var (session, applier, clock) = Build(hourLocal: 23);

        // Bypass until the morning → the night block does not engage on tick.
        await applier.ApplyAsync(Desired(1, bypass: clock.LocalNow.AddHours(9)));
        await session.ProcessTickAsync();
        session.GetCurrentState().Status.Should().NotBe("NightBlocked");

        // Clearing the bypass lets the night block engage again.
        await applier.ApplyAsync(Desired(2, bypass: null));
        await session.ProcessTickAsync();
        session.GetCurrentState().Status.Should().Be("NightBlocked");
    }
}
