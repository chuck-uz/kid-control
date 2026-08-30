using FluentAssertions;
using KidControl.Domain.Monitoring;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class ContentMonitorTests
{
    private static ContentMonitor Monitor(
        string[]? profanity = null, string[]? adultWords = null,
        string[]? adultDomains = null, string[]? exceptions = null)
        => new(profanity ?? [], adultWords ?? [], adultDomains ?? [], exceptions ?? []);

    [Fact]
    public void Profanity_in_keyboard_text_hits_with_context_and_source()
    {
        var m = Monitor(profanity: ["хуй"]);

        var hit = m.ScanText("иди на хуй", MonitorSource.Keyboard, "иди на хуй");

        hit.Should().NotBeNull();
        hit!.Category.Should().Be(MonitorCategory.Profanity);
        hit.Term.Should().Be("хуй");
        hit.Source.Should().Be(MonitorSource.Keyboard);
        hit.Context.Should().Be("иди на хуй"); // raw context passed through verbatim
    }

    [Fact]
    public void Adult_keyword_takes_priority_over_profanity()
    {
        var m = Monitor(profanity: ["сука"], adultWords: ["порно"]);

        var hit = m.ScanText("смотрю порно сука", MonitorSource.Keyboard, "…");

        hit!.Category.Should().Be(MonitorCategory.Adult);
        hit.Term.Should().Be("порно");
    }

    [Fact]
    public void Adult_domain_in_url_hits_as_adult()
    {
        var m = Monitor(adultDomains: ["pornhub.com"]);

        var hit = m.ScanUrl("https://www.pornhub.com/view?x=1");

        hit!.Category.Should().Be(MonitorCategory.Adult);
        hit.Term.Should().Be("pornhub.com");
        hit.Source.Should().Be(MonitorSource.Url);
    }

    [Fact]
    public void Adult_keyword_in_url_on_neutral_host_hits()
    {
        var m = Monitor(adultWords: ["porno"], adultDomains: ["pornhub.com"]);

        // Neutral host, adult query in the URL text.
        var hit = m.ScanUrl("https://www.youtube.com/results?search_query=porno");

        hit!.Category.Should().Be(MonitorCategory.Adult);
        hit.Source.Should().Be(MonitorSource.Url);
    }

    [Fact]
    public void Exception_suppresses_a_root_inside_an_allowed_word()
    {
        var m = Monitor(profanity: ["бля"], exceptions: ["бляха"]);

        m.ScanText("вот бляха на колесе", MonitorSource.Keyboard, "…").Should().BeNull();
        m.ScanText("вот бля", MonitorSource.Keyboard, "…").Should().NotBeNull();
    }

    [Fact]
    public void Clean_text_and_url_do_not_hit()
    {
        var m = Monitor(profanity: ["сука"], adultWords: ["порно"], adultDomains: ["pornhub.com"]);

        m.ScanText("сегодня хорошая погода", MonitorSource.Window, "…").Should().BeNull();
        m.ScanUrl("https://www.wikipedia.org/wiki/Cat").Should().BeNull();
    }

    [Fact]
    public void Empty_monitor_never_hits()
    {
        ContentMonitor.Empty.IsEmpty.Should().BeTrue();
        ContentMonitor.Empty.ScanText("иди на хуй", MonitorSource.Keyboard, "x").Should().BeNull();
        ContentMonitor.Empty.ScanUrl("https://pornhub.com").Should().BeNull();
    }
}
