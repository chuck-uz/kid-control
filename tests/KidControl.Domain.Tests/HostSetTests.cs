using FluentAssertions;
using KidControl.Domain.Monitoring;
using Xunit;

namespace KidControl.Domain.Tests;

public sealed class HostSetTests
{
    private static readonly HostSet Adult = new(new[] { "pornhub.com", "www.xvideos.com" });

    [Theory]
    [InlineData("pornhub.com")]
    [InlineData("www.pornhub.com")]
    [InlineData("cdn.media.pornhub.com")]
    [InlineData("xvideos.com")]            // list had it as www.xvideos.com → stored as xvideos.com
    [InlineData("m.xvideos.com")]
    public void Matches_domain_and_subdomains(string host)
        => Adult.TryMatch(host, out _).Should().BeTrue();

    [Theory]
    [InlineData("notpornhub.com")]          // suffix must be on a label boundary
    [InlineData("pornhub.com.evil.com")]    // domain in the middle must not match
    [InlineData("example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_match_unrelated_or_spoofed_hosts(string? host)
        => Adult.TryMatch(host, out _).Should().BeFalse();

    [Fact]
    public void TryMatch_returns_the_listed_domain()
    {
        Adult.TryMatch("cdn.pornhub.com", out var domain).Should().BeTrue();
        domain.Should().Be("pornhub.com");
    }

    [Theory]
    [InlineData("https://www.PornHub.com/view?x=1", "www.pornhub.com")]
    [InlineData("pornhub.com/some/path", "pornhub.com")]
    [InlineData("http://a.b.example.com:8080/p", "a.b.example.com")]
    [InlineData("HTTPS://Example.COM", "example.com")]
    public void HostOf_extracts_lowercased_host(string url, string expected)
        => HostSet.HostOf(url).Should().Be(expected);

    [Fact]
    public void Url_of_an_adult_site_matches_via_hostof()
    {
        var host = HostSet.HostOf("https://www.pornhub.com/video/123?utm=x");
        Adult.TryMatch(host, out _).Should().BeTrue();
    }
}
