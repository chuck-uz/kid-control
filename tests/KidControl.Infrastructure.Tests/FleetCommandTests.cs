using FluentAssertions;
using KidControl.Application.Commands;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public class FleetCommandTests
{
    private static CommandDto Cmd(string type, Dictionary<string, string>? payload = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = type,
        Payload = payload,
        TtlAt = DateTimeOffset.UtcNow.AddMinutes(5)
    };

    [Fact]
    public void AddTime_maps_to_AddTime_command()
    {
        var mapped = FleetCommandApplier.ToSessionCommand(
            Cmd(CommandTypes.AddTime, new Dictionary<string, string> { ["minutes"] = "30" }));
        mapped.Should().BeOfType<SessionCommand.AddTime>().Which.Minutes.Should().Be(30);
    }

    [Fact]
    public void AddTime_without_positive_minutes_is_unsupported()
    {
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.AddTime)).Should().BeNull();
        FleetCommandApplier.ToSessionCommand(
            Cmd(CommandTypes.AddTime, new Dictionary<string, string> { ["minutes"] = "0" })).Should().BeNull();
    }

    [Fact]
    public void ResetTimer_shutdown_restart_map_to_session_commands()
    {
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.ResetTimer))
            .Should().BeOfType<SessionCommand.ResetTimer>();
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.Shutdown))
            .Should().BeOfType<SessionCommand.ShutdownPc>();
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.Restart))
            .Should().BeOfType<SessionCommand.RestartPc>();
    }

    [Fact]
    public void UpdateNow_and_media_are_not_session_commands()
    {
        // Handled in ApplyAsync, not the pure session-command map.
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.UpdateNow)).Should().BeNull();
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.Screenshot)).Should().BeNull();
    }

    [Fact]
    public void Processed_store_dedupes_and_bounds_history()
    {
        var store = new JsonProcessedCommandStore(NullLogger<JsonProcessedCommandStore>.Instance);

        var first = Guid.NewGuid().ToString();
        store.Contains(first).Should().BeFalse();
        store.Add(first);
        store.Contains(first).Should().BeTrue();
        store.Add(first); // no-op, still one entry

        // Bound: after many adds the oldest ages out, newest stays.
        for (var i = 0; i < 300; i++)
            store.Add(Guid.NewGuid().ToString());
        var newest = Guid.NewGuid().ToString();
        store.Add(newest);

        store.Contains(newest).Should().BeTrue();
        store.Contains(first).Should().BeFalse(); // evicted
    }
}
