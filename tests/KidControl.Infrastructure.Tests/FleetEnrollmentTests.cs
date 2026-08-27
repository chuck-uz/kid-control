using System.Net;
using System.Text;
using FluentAssertions;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public class FleetEnrollmentTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static FleetClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://fleet.test/") },
            NullLogger<FleetClient>.Instance);

    private static readonly AgentInfo Agent = new("KID-PC", "Windows 11", "2.0.11");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T10:00:00Z");

    private static FleetEnrollmentService Service(FleetConfig cfg, FleetClient client, IDeviceIdentityStore store)
        => new(cfg, client, store, Agent, new TestClock(Now), NullLogger<FleetEnrollmentService>.Instance);

    [Fact]
    public void Standalone_when_no_url()
    {
        new FleetConfig().IsManaged.Should().BeFalse();
        new FleetConfig { Url = "https://x" }.IsManaged.Should().BeTrue();
    }

    [Fact]
    public async Task Standalone_config_does_not_enroll()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var store = new Mock<IDeviceIdentityStore>();

        var step = await Service(new FleetConfig(), ClientFor(handler), store.Object).EnsureEnrolledAsync();

        step.Should().Be(EnrollmentStep.NotManaged);
        handler.Calls.Should().Be(0);
        store.Verify(s => s.Save(It.IsAny<DeviceIdentity>()), Times.Never);
    }

    [Fact]
    public async Task Managed_with_code_enrolls_and_persists_identity()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"deviceId":"11111111-1111-1111-1111-111111111111","token":"secret-token"}""");
        var store = new Mock<IDeviceIdentityStore>();
        store.Setup(s => s.Load()).Returns((DeviceIdentity?)null);
        var cfg = new FleetConfig { Url = "https://fleet.test", EnrollCode = "K7Q2-9F3M" };

        var step = await Service(cfg, ClientFor(handler), store.Object).EnsureEnrolledAsync();

        step.Should().Be(EnrollmentStep.Enrolled);
        handler.Calls.Should().Be(1);
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://fleet.test/agent/enroll");
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);

        // Sends the configured code + this agent's facts.
        var sent = FleetJson.Deserialize<EnrollRequest>(handler.LastBody!)!;
        sent.Code.Should().Be("K7Q2-9F3M");
        sent.MachineName.Should().Be("KID-PC");

        // Persists the returned identity (token included) exactly once.
        store.Verify(s => s.Save(It.Is<DeviceIdentity>(d =>
            d.DeviceId == "11111111-1111-1111-1111-111111111111" &&
            d.Token == "secret-token" &&
            d.BackendUrl == "https://fleet.test" &&
            d.EnrolledAt == Now)), Times.Once);
    }

    [Fact]
    public async Task Already_enrolled_skips_http_and_reuses_token()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var store = new Mock<IDeviceIdentityStore>();
        store.Setup(s => s.Load()).Returns(new DeviceIdentity("dev-1", "cached-token", Now, "https://fleet.test"));
        var cfg = new FleetConfig { Url = "https://fleet.test", EnrollCode = "K7Q2-9F3M" };

        var step = await Service(cfg, ClientFor(handler), store.Object).EnsureEnrolledAsync();

        step.Should().Be(EnrollmentStep.AlreadyEnrolled);
        handler.Calls.Should().Be(0); // no re-enroll
        store.Verify(s => s.Save(It.IsAny<DeviceIdentity>()), Times.Never);
    }

    [Fact]
    public async Task Managed_without_code_and_not_enrolled_reports_no_code()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var store = new Mock<IDeviceIdentityStore>();
        store.Setup(s => s.Load()).Returns((DeviceIdentity?)null);
        var cfg = new FleetConfig { Url = "https://fleet.test" }; // no EnrollCode

        var step = await Service(cfg, ClientFor(handler), store.Object).EnsureEnrolledAsync();

        step.Should().Be(EnrollmentStep.NoCode);
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Used_code_returns_conflict_and_does_not_persist()
    {
        var handler = new StubHandler(HttpStatusCode.Conflict, """{"error":"code already used"}""");
        var store = new Mock<IDeviceIdentityStore>();
        store.Setup(s => s.Load()).Returns((DeviceIdentity?)null);
        var cfg = new FleetConfig { Url = "https://fleet.test", EnrollCode = "USED-CODE" };

        var step = await Service(cfg, ClientFor(handler), store.Object).EnsureEnrolledAsync();

        step.Should().Be(EnrollmentStep.Failed);
        store.Verify(s => s.Save(It.IsAny<DeviceIdentity>()), Times.Never);
    }

    [Fact]
    public async Task Backend_unreachable_is_non_fatal()
    {
        // A handler that throws simulates the backend being down; the agent must not crash.
        var client = new FleetClient(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://fleet.test/") },
            NullLogger<FleetClient>.Instance);
        var store = new Mock<IDeviceIdentityStore>();
        store.Setup(s => s.Load()).Returns((DeviceIdentity?)null);
        var cfg = new FleetConfig { Url = "https://fleet.test", EnrollCode = "K7Q2-9F3M" };

        var step = await Service(cfg, client, store.Object).EnsureEnrolledAsync();

        step.Should().Be(EnrollmentStep.Failed);
        store.Verify(s => s.Save(It.IsAny<DeviceIdentity>()), Times.Never);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }
}
