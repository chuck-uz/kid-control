using FluentAssertions;
using KidControl.Backend.Fleet;
using Xunit;

namespace KidControl.Backend.Tests;

public class AlertServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-29T10:00:00Z");
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(3);

    [Fact]
    public void Never_seen_is_offline()
        => AlertBackgroundService.IsOffline(null, Now, Threshold).Should().BeTrue();

    [Fact]
    public void Recent_heartbeat_is_online()
        => AlertBackgroundService.IsOffline(Now.AddSeconds(-30), Now, Threshold).Should().BeFalse();

    [Fact]
    public void Just_within_threshold_is_online()
        => AlertBackgroundService.IsOffline(Now.AddMinutes(-2), Now, Threshold).Should().BeFalse();

    [Fact]
    public void Past_threshold_is_offline()
        => AlertBackgroundService.IsOffline(Now.AddMinutes(-4), Now, Threshold).Should().BeTrue();
}
