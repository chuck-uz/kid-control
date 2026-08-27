using FluentAssertions;
using KidControl.Backend.Entities;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace KidControl.Backend.Tests;

public class EnrollmentServiceTests
{
    // A clock we can advance; the service takes TimeProvider so codes can be expired in tests.
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static FleetDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseInMemoryDatabase($"enroll-{Guid.NewGuid()}")
            // BeginTransaction is a no-op on the in-memory store; don't let that warning throw.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new FleetDbContext(options);
    }

    private static EnrollRequest Req(string code) =>
        new(code, "KID-PC", OsInfo: "Windows 11", AgentVersion: "2.0.11");

    [Fact]
    public async Task Valid_code_creates_device_and_issues_token()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-27T10:00:00Z"));
        await using var db = NewDb();
        var svc = new EnrollmentService(db, clock);

        var code = await svc.CreateCodeAsync();
        var result = await svc.EnrollAsync(Req(code.Code));

        result.Ok.Should().BeTrue();
        result.Response!.Token.Should().NotBeNullOrWhiteSpace();
        var deviceId = Guid.Parse(result.Response.DeviceId);

        var device = await db.Devices.SingleAsync();
        device.Id.Should().Be(deviceId);
        device.Name.Should().Be("KID-PC");
        device.Revoked.Should().BeFalse();
        // Only the hash is stored — never the plaintext token.
        device.TokenHash.Should().Be(FleetTokens.HashToken(result.Response.Token));
        db.Devices.All(d => d.TokenHash != result.Response.Token).Should().BeTrue();

        // Default policy + desired provisioned so the first heartbeat has state.
        (await db.DevicePolicies.SingleAsync()).DeviceId.Should().Be(deviceId);
        (await db.DeviceDesired.SingleAsync()).DeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task Code_is_single_use()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-27T10:00:00Z"));
        await using var db = NewDb();
        var svc = new EnrollmentService(db, clock);

        var code = await svc.CreateCodeAsync();
        (await svc.EnrollAsync(Req(code.Code))).Ok.Should().BeTrue();

        var second = await svc.EnrollAsync(Req(code.Code));
        second.Ok.Should().BeFalse();
        second.Error.Should().Be(EnrollError.AlreadyUsed);

        (await db.Devices.CountAsync()).Should().Be(1); // no second device
    }

    [Fact]
    public async Task Accepts_display_form_with_dashes_and_lowercase()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-27T10:00:00Z"));
        await using var db = NewDb();
        var svc = new EnrollmentService(db, clock);

        var code = await svc.CreateCodeAsync();
        // Operator/agent may type it messily; normalization must still match.
        var messy = code.Code.ToLowerInvariant().Replace("-", " ");
        (await svc.EnrollAsync(Req(messy))).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_code_is_rejected()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-27T10:00:00Z"));
        await using var db = NewDb();
        var svc = new EnrollmentService(db, clock);

        var result = await svc.EnrollAsync(Req("ZZZZ-ZZZZ"));
        result.Error.Should().Be(EnrollError.InvalidCode);
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-27T10:00:00Z"));
        await using var db = NewDb();
        var svc = new EnrollmentService(db, clock);

        var code = await svc.CreateCodeAsync(TimeSpan.FromMinutes(10));
        clock.Now = clock.Now.AddMinutes(11); // past expiry

        var result = await svc.EnrollAsync(Req(code.Code));
        result.Error.Should().Be(EnrollError.Expired);
        (await db.Devices.CountAsync()).Should().Be(0);
    }
}
