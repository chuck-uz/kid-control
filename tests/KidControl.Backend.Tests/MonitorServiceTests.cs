using FluentAssertions;
using KidControl.Backend.Entities;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Xunit;

namespace KidControl.Backend.Tests;

public sealed class MonitorServiceTests
{
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

    private static FleetDbContext NewDb() =>
        new(new DbContextOptionsBuilder<FleetDbContext>()
            .UseInMemoryDatabase($"mon-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<Guid> SeedDeviceAsync(FleetDbContext db)
    {
        var id = Guid.NewGuid();
        db.Devices.Add(new Device { Id = id, Name = "KID-PC", TokenHash = "h", EnrolledAt = T0 });
        db.DevicePolicies.Add(new DevicePolicy { DeviceId = id, Version = 3, UpdatedAt = T0 });
        await db.SaveChangesAsync();
        return id;
    }

    private static MonitorListsDto Lists(int _ = 0) => new()
    {
        Profanity = ["сука", "бля"],
        AdultKeywords = ["порно"],
        AdultDomains = ["pornhub.com", "pornhub.com"], // dup dropped
        Exceptions = ["бляха"]
    };

    // ── MonitorListService ────────────────────────────────────────────────────
    [Fact]
    public async Task ReplaceAll_stores_terms_and_bumps_version()
    {
        await using var db = NewDb();
        var svc = new MonitorListService(db, new TestClock(T0));

        (await svc.GetVersionAsync()).Should().Be(0);
        var v = await svc.ReplaceAllAsync(Lists());
        v.Should().Be(1);

        var got = await svc.GetListsAsync();
        got.Version.Should().Be(1);
        got.Profanity.Should().BeEquivalentTo("сука", "бля");
        got.AdultDomains.Should().ContainSingle().Which.Should().Be("pornhub.com"); // dedup
        got.Exceptions.Should().ContainSingle();
    }

    [Fact]
    public async Task ReplaceAll_bumps_device_policy_versions_so_agents_refetch()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var svc = new MonitorListService(db, new TestClock(T0));

        await svc.ReplaceAllAsync(Lists());

        var policy = await db.DevicePolicies.FindAsync(id);
        policy!.Version.Should().Be(4); // was 3, bumped so the new lists version propagates
    }

    // ── WordAlertService ──────────────────────────────────────────────────────
    private static WordAlertService AlertSvc(FleetDbContext db, TestClock clock, WordAlertTracker tracker) =>
        new(db, clock, tracker, new DbAdminRegistry(db),
            new TelegramBotClient("0:DISABLED"), NullLogger<WordAlertService>.Instance);

    private static WordAlertDto Hit(string term = "сука", string category = "profanity") =>
        new() { Category = category, Term = term, Source = "keyboard", Context = "иди на " + term };

    [Fact]
    public async Task Alert_records_metadata_only_and_dedupes()
    {
        await using var db = NewDb();
        var id = await SeedDeviceAsync(db);
        var clock = new TestClock(T0);
        var svc = AlertSvc(db, clock, new WordAlertTracker());

        (await svc.HandleAsync(id, Hit(), screenshot: null)).Should().BeTrue();
        (await svc.HandleAsync(id, Hit(), screenshot: null)).Should().BeTrue(); // duplicate within cooldown

        var alerts = await db.WordAlerts.ToListAsync();
        alerts.Should().ContainSingle(); // the duplicate was suppressed → only one row
        alerts[0].Category.Should().Be("profanity");
        alerts[0].Term.Should().Be("сука");
        alerts[0].Source.Should().Be("keyboard");
    }

    [Fact]
    public async Task Alert_for_unknown_device_is_rejected()
    {
        await using var db = NewDb();
        var svc = AlertSvc(db, new TestClock(T0), new WordAlertTracker());

        (await svc.HandleAsync(Guid.NewGuid(), Hit(), null)).Should().BeFalse();
        (await db.WordAlerts.CountAsync()).Should().Be(0);
    }
}
