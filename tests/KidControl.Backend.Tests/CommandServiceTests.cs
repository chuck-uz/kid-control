using FluentAssertions;
using KidControl.Backend.Entities;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace KidControl.Backend.Tests;

public class CommandServiceTests
{
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-27T10:00:00Z");

    private static FleetDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseInMemoryDatabase($"cmd-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FleetDbContext(options);
    }

    private static async Task<Guid> SeedDeviceAsync(FleetDbContext db)
    {
        var id = Guid.NewGuid();
        db.Devices.Add(new Device { Id = id, Name = "KID-PC", TokenHash = "h", EnrolledAt = T0 });
        db.DeviceDesired.Add(new DeviceDesired { DeviceId = id, Version = 1 });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Enqueue_then_poll_returns_command_and_marks_delivered()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var clock = new TestClock(T0);
        var svc = new CommandService(db, clock, new CommandSignal());

        var cmdId = await svc.EnqueueAsync(id, CommandTypes.AddTime,
            new Dictionary<string, string> { ["minutes"] = "30" }, TimeSpan.FromMinutes(5));

        var due = await svc.PollAsync(id, TimeSpan.Zero, CancellationToken.None);

        due.Should().HaveCount(1);
        due[0].Id.Should().Be(cmdId!.Value.ToString());
        due[0].Type.Should().Be(CommandTypes.AddTime);
        due[0].GetInt("minutes").Should().Be(30);

        var row = await db.Commands.FindAsync(cmdId.Value);
        row!.DeliveredAt.Should().Be(T0);
    }

    [Fact]
    public async Task Acked_command_is_not_delivered_again()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var svc = new CommandService(db, new TestClock(T0), new CommandSignal());
        var cmdId = (await svc.EnqueueAsync(id, CommandTypes.AddTime, null, TimeSpan.FromMinutes(5)))!.Value;

        await svc.PollAsync(id, TimeSpan.Zero, CancellationToken.None);
        await svc.AckAsync(id, new CommandAckBatch([new CommandAckDto(cmdId.ToString(), true)]));

        (await svc.PollAsync(id, TimeSpan.Zero, CancellationToken.None)).Should().BeEmpty();
        (await db.Commands.FindAsync(cmdId))!.Result.Should().Be("ok");
    }

    [Fact]
    public async Task Ack_is_idempotent()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var clock = new TestClock(T0);
        var svc = new CommandService(db, clock, new CommandSignal());
        var cmdId = (await svc.EnqueueAsync(id, CommandTypes.AddTime, null, TimeSpan.FromMinutes(5)))!.Value;

        await svc.AckAsync(id, new CommandAckBatch([new CommandAckDto(cmdId.ToString(), true)]));
        var firstAckedAt = (await db.Commands.FindAsync(cmdId))!.AckedAt;

        clock.Now = T0.AddMinutes(1);
        // A second ack (e.g. after redelivery) must not overwrite or throw.
        await svc.AckAsync(id, new CommandAckBatch([new CommandAckDto(cmdId.ToString(), false, "late")]));

        var row = await db.Commands.FindAsync(cmdId);
        row!.AckedAt.Should().Be(firstAckedAt);
        row.Result.Should().Be("ok");
    }

    [Fact]
    public async Task Expired_command_is_not_delivered()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var clock = new TestClock(T0);
        var svc = new CommandService(db, clock, new CommandSignal());
        await svc.EnqueueAsync(id, CommandTypes.AddTime, null, TimeSpan.FromMinutes(1));

        clock.Now = T0.AddMinutes(2); // past TTL

        (await svc.PollAsync(id, TimeSpan.Zero, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Poll_with_no_commands_returns_empty()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var svc = new CommandService(db, new TestClock(T0), new CommandSignal());

        (await svc.PollAsync(id, TimeSpan.Zero, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Enqueue_to_unknown_device_returns_null()
    {
        await using var db = NewDb();
        var svc = new CommandService(db, new TestClock(T0), new CommandSignal());
        (await svc.EnqueueAsync(Guid.NewGuid(), CommandTypes.AddTime, null, TimeSpan.FromMinutes(5)))
            .Should().BeNull();
    }

    [Fact]
    public async Task SetPaused_bumps_version_then_is_idempotent()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var admin = new DeviceAdminService(db, new TestClock(T0));

        (await admin.SetPausedAsync(id, true)).Should().Be(2);   // 1 -> 2
        (await admin.SetPausedAsync(id, true)).Should().Be(2);   // same value, no bump
        (await admin.SetPausedAsync(id, false)).Should().Be(3);  // change again

        (await db.DeviceDesired.FindAsync(id))!.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task SetForceBlocked_bumps_version_then_is_idempotent()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var admin = new DeviceAdminService(db, new TestClock(T0));

        (await admin.SetForceBlockedAsync(id, true)).Should().Be(2);
        (await admin.SetForceBlockedAsync(id, true)).Should().Be(2);  // no change → no bump
        (await db.DeviceDesired.FindAsync(id))!.ForceBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task SetNightBypass_sets_and_clears_with_version_bumps()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var admin = new DeviceAdminService(db, new TestClock(T0));
        var until = T0.AddHours(9);

        (await admin.SetNightBypassAsync(id, until)).Should().Be(2);
        (await admin.SetNightBypassAsync(id, until)).Should().Be(2);   // same value, no bump
        (await db.DeviceDesired.FindAsync(id))!.NightBypassUntil.Should().Be(until);

        (await admin.SetNightBypassAsync(id, null)).Should().Be(3);    // cleared
        (await db.DeviceDesired.FindAsync(id))!.NightBypassUntil.Should().BeNull();
    }
}
