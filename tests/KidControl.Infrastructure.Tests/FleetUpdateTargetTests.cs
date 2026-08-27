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

public class FleetUpdateTargetTests
{
    [Fact]
    public void Default_is_latest_and_not_pinned()
    {
        var t = new FleetUpdateTarget();
        t.Current.Should().Be("latest");
        t.IsPinned.Should().BeFalse();
    }

    [Fact]
    public void Set_pins_a_tag()
    {
        var t = new FleetUpdateTarget();
        t.Set("v2.0.10");
        t.Current.Should().Be("v2.0.10");
        t.IsPinned.Should().BeTrue();
        t.Set("  "); // blank ignored
        t.Current.Should().Be("v2.0.10");
    }

    [Theory]
    [InlineData("2.0.10", "latest", false)]        // tracking latest → never a pinned install
    [InlineData("2.0.10", "v2.0.10", false)]       // same version (v-prefix) → no-op
    [InlineData("2.0.10", "2.0.10", false)]        // same version → no-op
    [InlineData("0.0.1-source", "0.0.1", false)]   // pre-release suffix dropped → same
    [InlineData("2.0.10", "v2.0.11", true)]        // upgrade
    [InlineData("2.0.11", "v2.0.9", true)]         // downgrade (pin to older)
    public void NeedsPinnedInstall_compares_normalized_versions(string current, string target, bool expected)
        => FleetUpdateTarget.NeedsPinnedInstall(current, target).Should().Be(expected);

    [Fact]
    public async Task Policy_apply_sets_the_update_target()
    {
        var clock = new StubClock();
        var ui = new Mock<IUiNotifier>();
        ui.Setup(x => x.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var tg = new Mock<ITelegramGateway>();
        tg.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var session = new SessionService(clock, new Mock<ISessionStore>().Object, ui.Object, tg.Object,
            new Mock<ISystemController>().Object, NullLogger<SessionService>.Instance);

        var target = new FleetUpdateTarget();
        var applier = new FleetPolicyApplier(session, target, NullLogger<FleetPolicyApplier>.Instance);

        await applier.ApplyAsync(new PolicyDto { Version = 3, TargetVersion = "v2.0.10" });

        target.Current.Should().Be("v2.0.10");
        target.IsPinned.Should().BeTrue();
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset LocalNow { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }
}
