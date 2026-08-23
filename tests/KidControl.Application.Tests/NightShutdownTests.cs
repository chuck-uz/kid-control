using FluentAssertions;
using KidControl.Application.Abstractions;
using KidControl.Application.Services;
using KidControl.Application.Tests.Fakes;
using KidControl.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Application.Tests;

public sealed class NightShutdownTests
{
    private static (SessionService svc, FakeClock clock, Mock<ISystemController> system) Build(int hourLocal)
    {
        // A moment inside the default 22:00–07:00 night window.
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, hourLocal, 0, 0, TimeSpan.Zero));

        var ui = new Mock<IUiNotifier>();
        ui.Setup(x => x.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var tg = new Mock<ITelegramGateway>();
        tg.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var system = new Mock<ISystemController>();
        system.Setup(x => x.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        var store = new Mock<ISessionStore>(); // Load() returns null

        var svc = new SessionService(clock, store.Object, ui.Object, tg.Object, system.Object,
            NullLogger<SessionService>.Instance);
        return (svc, clock, system);
    }

    [Fact]
    public async Task Night_Block_Shows_Countdown_And_Does_Not_Shut_Down_Immediately()
    {
        var (svc, _, system) = Build(hourLocal: 23);

        await svc.ProcessTickAsync();

        var state = svc.GetCurrentState();
        state.Status.Should().Be("NightBlocked");
        state.ShutdownInSeconds.Should().BeInRange(1, 60);
        system.Verify(s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Night_Block_Shuts_Down_After_The_Grace_Elapses()
    {
        var (svc, clock, system) = Build(hourLocal: 23);

        await svc.ProcessTickAsync();           // enter night, arm the 60s grace
        clock.Advance(TimeSpan.FromSeconds(61));
        await svc.ProcessTickAsync();           // grace elapsed -> shut down

        system.Verify(s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_Is_Fired_Only_Once_While_Still_Night()
    {
        var (svc, clock, system) = Build(hourLocal: 23);

        await svc.ProcessTickAsync();
        clock.Advance(TimeSpan.FromSeconds(61));
        await svc.ProcessTickAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        await svc.ProcessTickAsync();           // still night, must not re-issue

        system.Verify(s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
