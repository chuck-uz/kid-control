using FluentAssertions;
using KidControl.Application.Commands;
using Xunit;

namespace KidControl.Application.Tests;

public sealed class CommandParserTests
{
    [Fact]
    public void Parse_Should_Return_Status()
        => CommandParser.Parse("/status").Should().BeOfType<SessionCommand.Status>();

    [Fact]
    public void Parse_Should_Return_Block()
        => CommandParser.Parse("/block").Should().BeOfType<SessionCommand.Block>();

    [Fact]
    public void Parse_Should_Return_Unblock()
        => CommandParser.Parse("/unblock").Should().BeOfType<SessionCommand.Unblock>();

    [Fact]
    public void Parse_Should_Return_ResetTimer()
        => CommandParser.Parse("/reset").Should().BeOfType<SessionCommand.ResetTimer>();

    [Fact]
    public void Parse_Should_Return_Pause()
        => CommandParser.Parse("/pause").Should().BeOfType<SessionCommand.Pause>();

    [Fact]
    public void Parse_Should_Return_Resume()
        => CommandParser.Parse("/resume").Should().BeOfType<SessionCommand.Resume>();

    [Fact]
    public void Parse_Should_Return_ShutdownPc()
        => CommandParser.Parse("/shutdown").Should().BeOfType<SessionCommand.ShutdownPc>();

    [Fact]
    public void Parse_Should_Return_RestartPc()
        => CommandParser.Parse("/restart").Should().BeOfType<SessionCommand.RestartPc>();

    [Fact]
    public void Parse_Should_BeCaseInsensitive_And_TrimWhitespace()
    {
        CommandParser.Parse("  /STATUS  ").Should().BeOfType<SessionCommand.Status>();
    }

    // ─── /addtime ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Should_Return_AddTime_With_Minutes()
    {
        CommandParser.Parse("/addtime 30")
            .Should().BeOfType<SessionCommand.AddTime>()
            .Which.Minutes.Should().Be(30);
    }

    [Theory]
    [InlineData("/addtime")]
    [InlineData("/addtime abc")]
    [InlineData("/addtime 0")]
    [InlineData("/addtime -5")]
    public void Parse_Should_Return_Unknown_When_AddTimeInvalid(string text)
    {
        CommandParser.Parse(text)
            .Should().BeOfType<SessionCommand.Unknown>()
            .Which.Help.Should().NotBeNullOrWhiteSpace();
    }

    // ─── /setrule ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Should_Return_SetRule_With_Rule()
    {
        var cmd = CommandParser.Parse("/setrule 50 10").Should().BeOfType<SessionCommand.SetRule>().Subject;

        cmd.Rule.PlayMinutes.Should().Be(50);
        cmd.Rule.RestMinutes.Should().Be(10);
    }

    [Theory]
    [InlineData("/setrule")]
    [InlineData("/setrule 50")]
    [InlineData("/setrule 50 abc")]
    [InlineData("/setrule 0 10")]
    [InlineData("/setrule 50 -1")]
    public void Parse_Should_Return_Unknown_When_SetRuleInvalid(string text)
    {
        CommandParser.Parse(text)
            .Should().BeOfType<SessionCommand.Unknown>()
            .Which.Help.Should().NotBeNullOrWhiteSpace();
    }

    // ─── /night ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Should_Return_SetNight_With_Window()
    {
        var cmd = CommandParser.Parse("/night 22:00-07:00").Should().BeOfType<SessionCommand.SetNight>().Subject;

        cmd.Window.Start.Should().Be(new TimeSpan(22, 0, 0));
        cmd.Window.End.Should().Be(new TimeSpan(7, 0, 0));
    }

    [Theory]
    [InlineData("/night")]
    [InlineData("/night 22:00")]
    [InlineData("/night 25:00-07:00")]
    [InlineData("/night garbage")]
    public void Parse_Should_Return_Unknown_When_NightInvalid(string text)
    {
        CommandParser.Parse(text)
            .Should().BeOfType<SessionCommand.Unknown>()
            .Which.Help.Should().NotBeNullOrWhiteSpace();
    }

    // ─── Unknown / empty ────────────────────────────────────────────────────

    [Theory]
    [InlineData("/nope")]
    [InlineData("hello")]
    [InlineData("status")] // missing leading slash
    public void Parse_Should_Return_Unknown_When_VerbUnrecognized(string text)
    {
        CommandParser.Parse(text)
            .Should().BeOfType<SessionCommand.Unknown>()
            .Which.Help.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void Parse_Should_Return_Unknown_When_Empty(string? text)
    {
        CommandParser.Parse(text).Should().BeOfType<SessionCommand.Unknown>();
    }
}
