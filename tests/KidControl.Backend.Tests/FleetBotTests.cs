using FluentAssertions;
using KidControl.Backend.Entities;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace KidControl.Backend.Tests;

public class FleetBotTests
{
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-27T10:00:00Z");

    private static FleetDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseInMemoryDatabase($"bot-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new FleetDbContext(options);
        db.Tenants.Add(new Tenant { Id = Tenant.DefaultId, Name = "Семья" });
        db.SaveChanges();
        return db;
    }

    private static (FleetBotActions actions, FleetDbContext db, EnrollmentService enroll) Build(FleetDbContext db)
    {
        var clock = new TestClock(T0);
        var deviceAdmin = new DeviceAdminService(db, clock);
        var commands = new CommandService(db, clock, new CommandSignal());
        var enroll = new EnrollmentService(db, clock);
        var admins = new DbAdminRegistry(db);
        return (new FleetBotActions(deviceAdmin, commands, enroll, admins), db, enroll);
    }

    private static async Task<Guid> EnrollDeviceAsync(FleetBotActions actions, EnrollmentService enroll)
    {
        var code = await enroll.CreateCodeAsync();
        var result = await enroll.EnrollAsync(new EnrollRequest(code.Code, "KID-PC"));
        return Guid.Parse(result.Response!.DeviceId);
    }

    // ── DbAdminRegistry ────────────────────────────────────────────────────────
    [Fact]
    public async Task Admin_registry_add_remove_and_protect_last()
    {
        await using var db = NewDb();
        var admins = new DbAdminRegistry(db);

        (await admins.IsAdminAsync(111)).Should().BeFalse();
        (await admins.AddAsync(111, "папа")).Should().BeTrue();
        (await admins.AddAsync(111)).Should().BeFalse();  // duplicate
        (await admins.IsAdminAsync(111)).Should().BeTrue();

        (await admins.RemoveAsync(111)).Should().BeFalse(); // last admin — protected
        (await admins.AddAsync(222)).Should().BeTrue();
        (await admins.RemoveAsync(111)).Should().BeTrue();  // now safe
        (await admins.CountAsync()).Should().Be(1);
    }

    // ── FleetBotActions ─────────────────────────────────────────────────────────
    [Fact]
    public async Task Lists_enrolled_device_and_shows_status()
    {
        await using var db = NewDb();
        var (actions, _, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        (await actions.ListDevicesAsync()).Should().ContainSingle(d => d.Id == id);
        (await actions.StatusTextAsync(id)).Should().Contain("KID-PC");
    }

    [Fact]
    public async Task AddTime_enqueues_a_command()
    {
        await using var db = NewDb();
        var (actions, ctx, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        var reply = await actions.AddTimeAsync(id, 30);

        reply.Should().Contain("+30");
        var cmd = await ctx.Commands.SingleAsync();
        cmd.Type.Should().Be(CommandTypes.AddTime);
        cmd.DeviceId.Should().Be(id);
    }

    [Fact]
    public async Task Pause_and_rule_edits_bump_versions()
    {
        await using var db = NewDb();
        var (actions, ctx, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        (await actions.PauseAsync(id, true)).Should().Contain("desired v2");
        (await ctx.DeviceDesired.FindAsync(id))!.Paused.Should().BeTrue();

        (await actions.SetRuleAsync(id, 60, 15)).Should().Contain("политика v2");
        var policy = await ctx.DevicePolicies.FindAsync(id);
        policy!.PlayMinutes.Should().Be(60);
        policy.RestMinutes.Should().Be(15);
    }

    [Fact]
    public async Task Revoke_removes_device_from_the_list()
    {
        await using var db = NewDb();
        var (actions, _, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        (await actions.RevokeAsync(id)).Should().Contain("отозвано");
        (await actions.ListDevicesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Rename_changes_the_device_name()
    {
        await using var db = NewDb();
        var (actions, ctx, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        (await actions.RenameAsync(id, "  ПК Ромы  ")).Should().Contain("ПК Ромы");
        (await ctx.Devices.FindAsync(id))!.Name.Should().Be("ПК Ромы");

        (await actions.ListDevicesAsync()).Single(d => d.Id == id).Name.Should().Be("ПК Ромы");
    }

    [Fact]
    public async Task Rename_rejects_blank_and_unknown()
    {
        await using var db = NewDb();
        var (actions, _, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        (await actions.RenameAsync(id, "   ")).Should().Contain("Не удалось");
        (await actions.RenameAsync(Guid.NewGuid(), "X")).Should().Contain("Не удалось");
    }

    [Fact]
    public async Task History_lists_recent_actions_readably()
    {
        await using var db = NewDb();
        var (actions, _, enroll) = Build(db);
        var id = await EnrollDeviceAsync(actions, enroll);

        await actions.SetRuleAsync(id, 60, 15);   // -> policy.edit
        await actions.PauseAsync(id, true);        // -> desired.pause
        await actions.RenameAsync(id, "ПК Ромы");  // -> device.rename

        var text = await actions.HistoryTextAsync(id);
        text.Should().Contain("История");
        text.Should().Contain("переименование");
        text.Should().Contain("пауза");
        text.Should().Contain("правка политики");
        text.Should().Contain("привязка устройства");
    }

    [Fact]
    public async Task History_is_empty_for_a_device_with_no_actions()
    {
        await using var db = NewDb();
        var (actions, ctx, _) = Build(db);
        var id = Guid.NewGuid();
        ctx.Devices.Add(new Device { Id = id, Name = "X", TokenHash = "h", EnrolledAt = T0 });
        await ctx.SaveChangesAsync();
        (await actions.HistoryTextAsync(id)).Should().Contain("пуста");
    }

    [Fact]
    public async Task New_enroll_code_is_issued()
    {
        await using var db = NewDb();
        var (actions, _, _) = Build(db);
        (await actions.NewEnrollCodeAsync()).Should().Contain("Код привязки");
    }

    [Fact]
    public async Task Unknown_device_replies_gracefully()
    {
        await using var db = NewDb();
        var (actions, _, _) = Build(db);
        (await actions.AddTimeAsync(Guid.NewGuid(), 30)).Should().Be("Устройство не найдено.");
        (await actions.SetRuleAsync(Guid.NewGuid(), 60, 15)).Should().Be("Устройство не найдено.");
    }
}
