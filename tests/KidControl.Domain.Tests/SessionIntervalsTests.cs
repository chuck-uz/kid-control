using FluentAssertions;
using KidControl.Domain.Entities;
using KidControl.Domain.Enums;
using KidControl.Domain.ValueObjects;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class SessionIntervalsTests
{
    private static Session Playing()
    {
        var s = new Session();
        s.ApplyRuleResettingPhase(new ScheduleRule(playMinutes: 10, restMinutes: 5));
        return s;
    }

    [Fact]
    public void DisableIntervals_Should_Freeze_Countdown_And_Never_Block()
    {
        var s = Playing();
        s.DisableIntervals();

        s.Tick(TimeSpan.FromHours(3));

        s.IntervalsEnabled.Should().BeFalse();
        s.Status.Should().Be(SessionStatus.Playing);
        // Remaining is not consumed while unlimited.
        s.TimeRemaining.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void DisableIntervals_While_Resting_Should_Return_To_Playing()
    {
        var s = Playing();
        s.StartRest();
        s.Status.Should().Be(SessionStatus.Resting);

        s.DisableIntervals();

        s.Status.Should().Be(SessionStatus.Playing);
    }

    [Fact]
    public void EnableIntervals_Should_Restart_A_Fresh_Play_Phase_And_Resume_Countdown()
    {
        var s = Playing();
        s.DisableIntervals();
        s.Tick(TimeSpan.FromHours(1)); // no effect while disabled

        s.EnableIntervals();
        s.IntervalsEnabled.Should().BeTrue();
        s.Status.Should().Be(SessionStatus.Playing);
        s.TimeRemaining.Should().Be(TimeSpan.FromMinutes(10));

        s.Tick(TimeSpan.FromMinutes(1));
        s.TimeRemaining.Should().Be(TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void NightBlock_Should_Still_Apply_When_Intervals_Disabled()
    {
        var s = Playing();
        s.DisableIntervals();

        s.EnterNight();
        s.Status.Should().Be(SessionStatus.NightBlocked);

        s.ExitNight();
        // Restores the pre-night status (Playing) and stays unlimited.
        s.Status.Should().Be(SessionStatus.Playing);
        s.IntervalsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Restore_Should_Rehydrate_IntervalsEnabled_Flag()
    {
        var s = Session.Restore(SessionStatus.Playing, TimeSpan.FromMinutes(3),
            new ScheduleRule(10, 5), intervalsEnabled: false);

        s.IntervalsEnabled.Should().BeFalse();
        s.Tick(TimeSpan.FromMinutes(5));
        s.TimeRemaining.Should().Be(TimeSpan.FromMinutes(3)); // frozen
    }
}
