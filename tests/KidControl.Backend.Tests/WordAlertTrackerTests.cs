using FluentAssertions;
using KidControl.Backend.Fleet;
using Xunit;

namespace KidControl.Backend.Tests;

public sealed class WordAlertTrackerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

    [Fact]
    public void Same_hit_within_cooldown_is_suppressed_then_allowed_after_60s()
    {
        var t = new WordAlertTracker();
        var dev = Guid.NewGuid();

        t.Decide(dev, "profanity", "сука", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Send);
        t.Decide(dev, "profanity", "сука", "keyboard", T0.AddSeconds(30)).Should().Be(WordAlertTracker.Decision.Suppress);
        t.Decide(dev, "profanity", "сука", "keyboard", T0.AddSeconds(61)).Should().Be(WordAlertTracker.Decision.Send);
    }

    [Fact]
    public void Different_term_or_source_is_not_deduped()
    {
        var t = new WordAlertTracker();
        var dev = Guid.NewGuid();

        t.Decide(dev, "profanity", "сука", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Send);
        t.Decide(dev, "profanity", "бля", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Send);
        t.Decide(dev, "profanity", "сука", "window", T0).Should().Be(WordAlertTracker.Decision.Send);
    }

    [Fact]
    public void Per_device_ceiling_rolls_up_after_ten_then_suppresses_then_resets_next_minute()
    {
        var t = new WordAlertTracker();
        var dev = Guid.NewGuid();

        // 10 distinct terms in the same minute → all Send.
        for (var i = 0; i < 10; i++)
            t.Decide(dev, "profanity", $"w{i}", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Send);

        // 11th → a single rollup notice; 12th+ → suppressed.
        t.Decide(dev, "profanity", "w10", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Rollup);
        t.Decide(dev, "profanity", "w11", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Suppress);

        // New minute → sending resumes.
        t.Decide(dev, "profanity", "w12", "keyboard", T0.AddSeconds(61)).Should().Be(WordAlertTracker.Decision.Send);
    }

    [Fact]
    public void Ceiling_is_per_device()
    {
        var t = new WordAlertTracker();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        for (var i = 0; i < 11; i++)
            t.Decide(a, "profanity", $"w{i}", "keyboard", T0);

        // b is unaffected by a hitting its ceiling.
        t.Decide(b, "profanity", "w0", "keyboard", T0).Should().Be(WordAlertTracker.Decision.Send);
    }
}
