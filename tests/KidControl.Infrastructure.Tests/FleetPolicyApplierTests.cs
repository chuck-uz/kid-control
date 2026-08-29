using FluentAssertions;
using KidControl.Application.Commands;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public class FleetPolicyApplierTests
{
    [Fact]
    public void Translates_policy_into_ordered_commands_with_rule_last()
    {
        var policy = new PolicyDto
        {
            Version = 5,
            PlayMinutes = 45,
            RestMinutes = 15,
            NightEnabled = false,
            NightStart = TimeSpan.FromHours(23),
            NightEnd = TimeSpan.FromHours(8),
            IntervalsEnabled = false
        };

        var commands = FleetPolicyApplier.ToCommands(policy);

        commands.Should().HaveCount(4);
        commands[0].Should().BeOfType<SessionCommand.SetNight>()
            .Which.Window.Should().Be(policy.ToNightWindow());
        commands[1].Should().BeOfType<SessionCommand.SetNightEnabled>()
            .Which.Enabled.Should().BeFalse();
        var rule = commands[2].Should().BeOfType<SessionCommand.SetRule>().Which.Rule;
        rule.PlayMinutes.Should().Be(45);
        rule.RestMinutes.Should().Be(15);
        // Intervals MUST be last: it has the final word on the countdown (clears it when off,
        // so applying the rule can't repopulate a leftover time on an unlimited device).
        commands[3].Should().BeOfType<SessionCommand.SetIntervals>()
            .Which.Enabled.Should().BeFalse();
    }

    [Fact]
    public void FleetState_versions_derive_from_cached_dtos()
    {
        var empty = new FleetState();
        empty.PolicyVersion.Should().Be(0);
        empty.DesiredVersion.Should().Be(0);

        var state = new FleetState
        {
            Policy = new PolicyDto { Version = 7 },
            Desired = new DesiredStateDto { Version = 4 }
        };
        state.PolicyVersion.Should().Be(7);
        state.DesiredVersion.Should().Be(4);
    }
}
