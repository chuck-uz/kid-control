using FluentAssertions;
using KidControl.Backend.Entities;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace KidControl.Backend.Tests;

public class HeartbeatServiceTests
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
            .UseInMemoryDatabase($"hb-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FleetDbContext(options);
    }

    private static async Task<Guid> SeedDeviceAsync(FleetDbContext db, int policyVersion = 3, int desiredVersion = 2)
    {
        var id = Guid.NewGuid();
        db.Devices.Add(new Device { Id = id, Name = "KID-PC", TokenHash = "h", EnrolledAt = T0 });
        db.DevicePolicies.Add(new DevicePolicy
        {
            DeviceId = id, Version = policyVersion, PlayMinutes = 45, RestMinutes = 15, UpdatedAt = T0
        });
        db.DeviceDesired.Add(new DeviceDesired { DeviceId = id, Version = desiredVersion, Paused = true, UpdatedAt = T0 });
        await db.SaveChangesAsync();
        return id;
    }

    private static HeartbeatRequest Beat(int policyVersion, int desiredVersion) => new()
    {
        Status = new StatusReportDto
        {
            Status = "Playing", TimeRemaining = TimeSpan.FromMinutes(30), AgentVersion = "2.0.11"
        },
        PolicyVersion = policyVersion,
        DesiredVersion = desiredVersion
    };

    [Fact]
    public async Task Stale_agent_receives_policy_and_desired_snapshot()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db, policyVersion: 3, desiredVersion: 2);
        var svc = new HeartbeatService(db, new TestClock(T0));

        var resp = await svc.HandleAsync(id, Beat(policyVersion: 1, desiredVersion: 1));

        resp!.Policy.Should().NotBeNull();
        resp.Policy!.Version.Should().Be(3);
        resp.Policy.PlayMinutes.Should().Be(45);
        resp.Desired.Should().NotBeNull();
        resp.Desired!.Version.Should().Be(2);
        resp.Desired.Paused.Should().BeTrue();
    }

    [Fact]
    public async Task Up_to_date_agent_receives_no_snapshot()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db, policyVersion: 3, desiredVersion: 2);
        var svc = new HeartbeatService(db, new TestClock(T0));

        var resp = await svc.HandleAsync(id, Beat(policyVersion: 3, desiredVersion: 2));

        resp!.Policy.Should().BeNull();
        resp.Desired.Should().BeNull();
    }

    [Fact]
    public async Task Heartbeat_records_status_and_liveness()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var clock = new TestClock(T0.AddMinutes(5));
        var svc = new HeartbeatService(db, clock);

        await svc.HandleAsync(id, Beat(3, 2));

        var device = await db.Devices.FindAsync(id);
        device!.LastSeenAt.Should().Be(clock.Now);
        device.AgentVersion.Should().Be("2.0.11");

        var status = await db.DeviceStatuses.SingleAsync();
        status.Status.Should().Be("Playing");
        status.TimeRemaining.Should().Be(TimeSpan.FromMinutes(30));
        status.ReportedAt.Should().Be(clock.Now);
    }

    [Fact]
    public async Task HasCommands_is_true_only_for_pending_non_expired_commands()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        db.Commands.Add(new Command
        {
            DeviceId = id, Type = CommandTypes.AddTime, CreatedAt = T0, TtlAt = T0.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        var svc = new HeartbeatService(db, new TestClock(T0.AddMinutes(1)));

        (await svc.HandleAsync(id, Beat(3, 2)))!.HasCommands.Should().BeTrue();

        // After TTL, the same command no longer counts.
        var later = new HeartbeatService(db, new TestClock(T0.AddMinutes(10)));
        (await later.HandleAsync(id, Beat(3, 2)))!.HasCommands.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_device_returns_null()
    {
        await using var db = NewDb();
        var svc = new HeartbeatService(db, new TestClock(T0));
        (await svc.HandleAsync(Guid.NewGuid(), Beat(1, 1))).Should().BeNull();
    }

    [Fact]
    public async Task Policy_edit_bumps_version_and_applies_patch()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db, policyVersion: 3);
        var admin = new DeviceAdminService(db, new TestClock(T0));

        var newVersion = await admin.UpdatePolicyAsync(id, new PolicyPatch(PlayMinutes: 60, IntervalsEnabled: false));

        newVersion.Should().Be(4);
        var policy = await db.DevicePolicies.FindAsync(id);
        policy!.PlayMinutes.Should().Be(60);
        policy.IntervalsEnabled.Should().BeFalse();
        policy.RestMinutes.Should().Be(15); // untouched by the patch
    }
}
