using FluentAssertions;
using KidControl.Domain.ValueObjects;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class NightWindowTests
{
    private static TimeSpan At(int h, int m = 0) => new(h, m, 0);

    [Fact]
    public void Default_Should_Be_22_To_07()
    {
        NightWindow.Default.Start.Should().Be(At(22));
        NightWindow.Default.End.Should().Be(At(7));
    }

    [Theory]
    [InlineData(0, 0, true)]   // just after midnight -> inside wrap window
    [InlineData(3, 0, true)]   // 03:00 inside
    [InlineData(23, 0, true)]  // 23:00 inside
    [InlineData(22, 0, true)]  // start is inclusive
    [InlineData(6, 59, true)]  // just before end
    [InlineData(7, 0, false)]  // end is exclusive
    [InlineData(9, 0, false)]  // daytime
    [InlineData(21, 59, false)]// just before start
    public void Contains_Should_Handle_WrapAroundWindow(int h, int m, bool expected)
    {
        var window = new NightWindow(At(22), At(7));

        window.Contains(At(h, m)).Should().Be(expected);
    }

    [Theory]
    [InlineData(8, 0, false)]  // before start
    [InlineData(9, 0, true)]   // start inclusive
    [InlineData(12, 0, true)]  // inside
    [InlineData(16, 59, true)] // just before end
    [InlineData(17, 0, false)] // end exclusive
    [InlineData(20, 0, false)] // after end
    public void Contains_Should_Handle_NonWrapWindow(int h, int m, bool expected)
    {
        var window = new NightWindow(At(9), At(17));

        window.Contains(At(h, m)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 0)]
    [InlineData(23, 59)]
    public void Contains_Should_AlwaysBeNight_When_StartEqualsEnd(int h, int m)
    {
        var window = new NightWindow(At(12), At(12));

        window.Contains(At(h, m)).Should().BeTrue();
    }

    [Fact]
    public void NextEnd_Should_ReturnSameDayEnd_When_StillBeforeEnd_InWrapWindow()
    {
        var window = new NightWindow(At(22), At(7));
        var now = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

        window.NextEnd(now).Should().Be(new DateTimeOffset(2026, 1, 1, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextEnd_Should_ReturnNextDayEnd_When_AfterStart_InWrapWindow()
    {
        var window = new NightWindow(At(22), At(7));
        var now = new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.Zero);

        window.NextEnd(now).Should().Be(new DateTimeOffset(2026, 1, 2, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextEnd_Should_ReturnSameDayEnd_ForNonWrapWindow()
    {
        var window = new NightWindow(At(9), At(17));
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        window.NextEnd(now).Should().Be(new DateTimeOffset(2026, 1, 1, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void NextEnd_Should_PreserveOffset()
    {
        var offset = TimeSpan.FromHours(5);
        var window = new NightWindow(At(22), At(7));
        var now = new DateTimeOffset(2026, 1, 1, 23, 0, 0, offset);

        window.NextEnd(now).Offset.Should().Be(offset);
    }

    [Fact]
    public void TryParse_Should_Parse_ValidInterval()
    {
        var ok = NightWindow.TryParse("21:30-08:00", out var window);

        ok.Should().BeTrue();
        window.Start.Should().Be(At(21, 30));
        window.End.Should().Be(At(8));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("22:00")]
    [InlineData("22:00-")]
    [InlineData("22:00_07:00")]
    [InlineData("25:00-07:00")]
    [InlineData("abc-def")]
    public void TryParse_Should_Fail_And_FallBackToDefault_When_Invalid(string? text)
    {
        var ok = NightWindow.TryParse(text, out var window);

        ok.Should().BeFalse();
        window.Should().Be(NightWindow.Default);
    }

    [Fact]
    public void Ctor_Should_Throw_When_StartOutOfDay()
    {
        var act = () => new NightWindow(TimeSpan.FromHours(24), At(7));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToString_Should_Render_HHmmInterval()
    {
        new NightWindow(At(22), At(7)).ToString().Should().Be("22:00-07:00");
    }
}
