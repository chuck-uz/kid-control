using FluentAssertions;
using KidControl.Domain.Entities;
using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class ComputerSessionTests
{
    [Fact]
    public void Tick_ShouldDecreaseTime_WhenStatusIsActive()
    {
        var session = new ComputerSession();
        session.SetRule(new ScheduleRule(playMinutes: 10, restMinutes: 5));
        session.AddTime(TimeSpan.FromMinutes(10));

        session.Tick(TimeSpan.FromMinutes(1));

        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(9));
        session.CurrentStatus.Should().Be(LockStatus.Active);
    }

    [Fact]
    public void Tick_ShouldSwitchToBlocked_WhenActiveTimeExpires()
    {
        var session = new ComputerSession();
        session.SetRule(new ScheduleRule(playMinutes: 10, restMinutes: 5));
        session.AddTime(TimeSpan.FromMinutes(1));

        session.Tick(TimeSpan.FromMinutes(1));

        session.CurrentStatus.Should().Be(LockStatus.Blocked);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ForceBlock_ShouldIgnoreTimerTransitions_UntilForceUnblock()
    {
        var session = new ComputerSession();
        session.SetRule(new ScheduleRule(playMinutes: 10, restMinutes: 5));
        session.AddTime(TimeSpan.FromMinutes(2));
        session.ForceBlock();

        session.Tick(TimeSpan.FromMinutes(5));

        session.CurrentStatus.Should().Be(LockStatus.ForceBlocked);
        session.TimeRemaining.Should().Be(TimeSpan.FromMinutes(2));
    }
}
