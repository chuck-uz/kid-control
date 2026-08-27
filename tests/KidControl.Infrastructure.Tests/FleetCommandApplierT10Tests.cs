using FluentAssertions;
using KidControl.Application.Abstractions;
using KidControl.Application.Commands;
using KidControl.Application.Services;
using KidControl.Contracts;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public class FleetCommandApplierT10Tests
{
    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset LocalNow { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private static CommandDto Cmd(string type, Dictionary<string, string>? payload = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type,
        Payload = payload,
        TtlAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private static (FleetCommandApplier applier, Mock<ISystemController> system, FakeUpdateService update, FleetUpdateTarget target) Build()
    {
        var ui = new Mock<IUiNotifier>();
        ui.Setup(x => x.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var tg = new Mock<ITelegramGateway>();
        tg.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var system = new Mock<ISystemController>();
        system.Setup(x => x.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        system.Setup(x => x.RestartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var session = new SessionService(new StubClock(), new Mock<ISessionStore>().Object, ui.Object, tg.Object,
            system.Object, NullLogger<SessionService>.Instance);
        var update = new FakeUpdateService();
        var target = new FleetUpdateTarget();
        var applier = new FleetCommandApplier(session, update, target, NullLogger<FleetCommandApplier>.Instance);
        return (applier, system, update, target);
    }

    [Fact]
    public async Task Shutdown_executes_and_acks_ok()
    {
        var (applier, system, _, _) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.Shutdown));
        ok.Should().BeTrue();
        system.Verify(s => s.ShutdownAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Restart_executes_and_acks_ok()
    {
        var (applier, system, _, _) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.Restart));
        ok.Should().BeTrue();
        system.Verify(s => s.RestartAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetTimer_executes_and_acks_ok()
    {
        var (applier, _, _, _) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.ResetTimer));
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateNow_with_explicit_tag_installs_that_tag()
    {
        var (applier, _, update, _) = Build();
        var (ok, _) = await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow, new() { ["tag"] = "v2.0.9" }));
        ok.Should().BeTrue();
        update.InstalledTag.Should().Be("v2.0.9");
    }

    [Fact]
    public async Task UpdateNow_without_tag_uses_pinned_target()
    {
        var (applier, _, update, target) = Build();
        target.Set("v2.0.8");
        await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow));
        update.InstalledTag.Should().Be("v2.0.8");
    }

    [Fact]
    public async Task UpdateNow_without_tag_or_pin_resolves_latest()
    {
        var (applier, _, update, _) = Build();
        update.LatestTag = "v2.0.12"; // a newer release is available
        await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow));
        update.InstalledTag.Should().Be("v2.0.12");
    }

    [Fact]
    public async Task UpdateNow_when_already_latest_is_a_noop_ok()
    {
        var (applier, _, update, _) = Build(); // LatestTag null → up to date
        var (ok, msg) = await applier.ApplyAsync(Cmd(CommandTypes.UpdateNow));
        ok.Should().BeTrue();
        msg.Should().Contain("up to date");
        update.InstallCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(CommandTypes.Screenshot)]
    [InlineData(CommandTypes.PlayAudio)]
    public async Task Media_commands_are_phase_2(string type)
    {
        var (applier, _, update, _) = Build();
        var (ok, error) = await applier.ApplyAsync(Cmd(type));
        ok.Should().BeFalse();
        error.Should().Contain("Phase 2");
        update.InstallCalls.Should().Be(0);
    }
}
