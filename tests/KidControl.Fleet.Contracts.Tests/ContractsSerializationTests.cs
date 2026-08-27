using FluentAssertions;
using KidControl.Domain.ValueObjects;
using KidControl.Fleet.Contracts;
using Xunit;

namespace KidControl.Fleet.Contracts.Tests;

public sealed class ContractsSerializationTests
{
    private static T RoundTrip<T>(T value) => FleetJson.Deserialize<T>(FleetJson.Serialize(value))!;

    [Fact]
    public void Policy_RoundTrips()
    {
        var p = new PolicyDto
        {
            Version = 7,
            PlayMinutes = 50,
            RestMinutes = 10,
            NightEnabled = false,
            NightStart = TimeSpan.FromHours(21),
            NightEnd = TimeSpan.FromHours(8),
            IntervalsEnabled = false,
            TargetVersion = "v2.0.10"
        };

        RoundTrip(p).Should().Be(p);
    }

    [Fact]
    public void Policy_Maps_To_Domain_Value_Objects()
    {
        var p = new PolicyDto { PlayMinutes = 45, RestMinutes = 15, NightStart = TimeSpan.FromHours(22), NightEnd = TimeSpan.FromHours(7) };

        p.ToScheduleRule().Should().Be(new ScheduleRule(45, 15));
        p.ToNightWindow().Should().Be(new NightWindow(TimeSpan.FromHours(22), TimeSpan.FromHours(7)));
    }

    [Fact]
    public void Policy_From_Domain_Preserves_Values()
    {
        var p = PolicyDto.From(new ScheduleRule(60, 15),
            new NightWindow(TimeSpan.FromHours(23), TimeSpan.FromHours(6)), version: 3,
            nightEnabled: false, intervalsEnabled: false, targetVersion: "v2.0.9");

        p.Version.Should().Be(3);
        p.PlayMinutes.Should().Be(60);
        p.RestMinutes.Should().Be(15);
        p.NightEnabled.Should().BeFalse();
        p.NightStart.Should().Be(TimeSpan.FromHours(23));
        p.IntervalsEnabled.Should().BeFalse();
        p.TargetVersion.Should().Be("v2.0.9");
    }

    [Fact]
    public void Deserialize_Is_Tolerant_Of_Unknown_And_Missing_Fields()
    {
        // Unknown "extra" ignored; missing fields fall back to record defaults.
        var json = """{ "version": 2, "playMinutes": 33, "extra": "ignored" }""";

        var p = FleetJson.Deserialize<PolicyDto>(json)!;

        p.Version.Should().Be(2);
        p.PlayMinutes.Should().Be(33);
        p.RestMinutes.Should().Be(20);          // default
        p.NightEnabled.Should().BeTrue();       // default
        p.TargetVersion.Should().Be("latest");  // default
    }

    [Fact]
    public void DesiredState_And_Status_And_Heartbeat_RoundTrip()
    {
        var desired = new DesiredStateDto { Version = 4, Paused = true, ForceBlocked = false, NightBypassUntil = DateTimeOffset.UnixEpoch.AddHours(30) };
        RoundTrip(desired).Should().Be(desired);

        var status = new StatusReportDto { Status = "Resting", TimeRemaining = TimeSpan.FromMinutes(12), IsNight = true, IsUnlimited = false, ShutdownInSeconds = 42, AgentVersion = "2.0.10" };
        RoundTrip(status).Should().Be(status);

        var hb = new HeartbeatRequest { Status = status, PolicyVersion = 5, DesiredVersion = 4 };
        RoundTrip(hb).Should().Be(hb);
    }

    [Fact]
    public void Command_Payload_Helpers_And_Ttl()
    {
        var cmd = new CommandDto
        {
            Id = "abc",
            Type = CommandTypes.AddTime,
            Payload = new Dictionary<string, string> { ["minutes"] = "30", ["tag"] = "v2.0.10" },
            TtlAt = DateTimeOffset.UnixEpoch.AddMinutes(5)
        };

        var back = RoundTrip(cmd);
        back.GetInt("minutes").Should().Be(30);
        back.GetString("tag").Should().Be("v2.0.10");
        back.GetInt("missing").Should().BeNull();
        back.IsExpired(DateTimeOffset.UnixEpoch).Should().BeFalse();
        back.IsExpired(DateTimeOffset.UnixEpoch.AddMinutes(6)).Should().BeTrue();
    }

    [Fact]
    public void Enroll_RoundTrips()
    {
        var req = new EnrollRequest("CODE123", "WIN-PC", "Windows 10", "2.0.10");
        RoundTrip(req).Should().Be(req);

        var resp = new EnrollResponse("dev-1", "tok-xyz");
        RoundTrip(resp).Should().Be(resp);
    }
}
