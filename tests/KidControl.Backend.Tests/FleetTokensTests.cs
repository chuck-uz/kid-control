using FluentAssertions;
using KidControl.Backend.Fleet;
using Xunit;

namespace KidControl.Backend.Tests;

public class FleetTokensTests
{
    [Fact]
    public void EnrollCode_is_dashed_uppercase_and_avoids_lookalikes()
    {
        var code = FleetTokens.NewEnrollCode();
        code.Should().MatchRegex("^[0-9A-HJ-NP-TV-Z]{4}-[0-9A-HJ-NP-TV-Z]{4}$");
        code.Should().NotContainAny("I", "O", "L", "U");
    }

    [Fact]
    public void NormalizeCode_strips_dashes_spaces_and_uppercases()
        => FleetTokens.NormalizeCode(" k7q2-9f3m ").Should().Be("K7Q29F3M");

    [Fact]
    public void DeviceToken_is_url_safe_and_unique()
    {
        var a = FleetTokens.NewDeviceToken();
        var b = FleetTokens.NewDeviceToken();
        a.Should().NotBe(b);
        a.Should().MatchRegex("^[A-Za-z0-9_-]+$"); // base64url, no padding
    }

    [Fact]
    public void HashToken_is_stable_lowercase_hex_sha256()
    {
        var token = FleetTokens.NewDeviceToken();
        var h1 = FleetTokens.HashToken(token);
        var h2 = FleetTokens.HashToken(token);
        h1.Should().Be(h2).And.HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        FleetTokens.HashToken(token + "x").Should().NotBe(h1);
    }
}
