using FluentAssertions;
using KidControl.Domain.ValueObjects;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class ScheduleRuleTests
{
    [Fact]
    public void Default_Should_Be_40_Play_And_20_Rest()
    {
        ScheduleRule.Default.PlayMinutes.Should().Be(40);
        ScheduleRule.Default.RestMinutes.Should().Be(20);
    }

    [Fact]
    public void Durations_Should_Match_Minutes()
    {
        var rule = new ScheduleRule(50, 10);

        rule.PlayDuration.Should().Be(TimeSpan.FromMinutes(50));
        rule.RestDuration.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(40, 0)]
    [InlineData(40, -5)]
    [InlineData(ScheduleRule.MaxMinutes + 1, 20)]
    [InlineData(40, ScheduleRule.MaxMinutes + 1)]
    public void Ctor_Should_Throw_When_OutOfRange(int play, int rest)
    {
        var act = () => new ScheduleRule(play, rest);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(ScheduleRule.MaxMinutes, ScheduleRule.MaxMinutes)]
    public void Ctor_Should_Accept_Boundary_Values(int play, int rest)
    {
        var act = () => new ScheduleRule(play, rest);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryParse_Should_Parse_ValidPair()
    {
        var ok = ScheduleRule.TryParse("50/10", out var rule);

        ok.Should().BeTrue();
        rule.PlayMinutes.Should().Be(50);
        rule.RestMinutes.Should().Be(10);
    }

    [Fact]
    public void TryParse_Should_TrimWhitespaceAroundNumbers()
    {
        var ok = ScheduleRule.TryParse(" 30 / 15 ", out var rule);

        ok.Should().BeTrue();
        rule.PlayMinutes.Should().Be(30);
        rule.RestMinutes.Should().Be(15);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("50")]
    [InlineData("50/")]
    [InlineData("50/10/5")]
    [InlineData("abc/10")]
    [InlineData("50/xyz")]
    [InlineData("0/10")]
    [InlineData("50/0")]
    [InlineData("-5/10")]
    [InlineData("2000/10")]
    public void TryParse_Should_Fail_And_FallBackToDefault_When_Invalid(string? text)
    {
        var ok = ScheduleRule.TryParse(text, out var rule);

        ok.Should().BeFalse();
        rule.Should().Be(ScheduleRule.Default);
    }
}
