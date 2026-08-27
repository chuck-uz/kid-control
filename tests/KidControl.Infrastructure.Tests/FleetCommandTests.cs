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
    public void ResetTimer_maps_and_unknown_verbs_are_unsupported()
    {
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.ResetTimer))
            .Should().BeOfType<SessionCommand.ResetTimer>();
        // Not yet handled in T7 (they arrive in T10) → null so the loop acks them as failed.
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.Shutdown)).Should().BeNull();
        FleetCommandApplier.ToSessionCommand(Cmd(CommandTypes.UpdateNow)).Should().BeNull();
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
