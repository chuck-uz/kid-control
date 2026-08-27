using FluentAssertions;
using KidControl.Application.Abstractions;
using KidControl.Application.Services;
using KidControl.Contracts;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KidControl.Infrastructure.Tests;

/// <summary>
/// Integration tests for the offline→reconnect reconciliation flow (RFC §6–8): a real
/// <see cref="SessionService"/> + the real appliers + in-memory caches, driven through a fake
/// backend so connectivity can be toggled at will.
/// </summary>
public class FleetReconcilerTests
{
    // ── Test doubles ─────────────────────────────────────────────────────────
    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public DateTimeOffset LocalNow { get; set; } = now;
    }

    private sealed class TimeProviderClock(TestClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.UtcNow;
    }

    private sealed class InMemoryStateStore(FleetState seed) : IFleetStateStore
    {
        private FleetState _state = seed;
        public FleetState Load() => _state;
        public void Save(FleetState state) => _state = state;
    }

    private sealed class InMemoryProcessedStore : IProcessedCommandStore
    {
        private readonly HashSet<string> _ids = [];
        public bool Contains(string commandId) => _ids.Contains(commandId);
        public void Add(string commandId) => _ids.Add(commandId);
        public void Save() { }
    }

    private sealed class FakeFleetClient : IFleetClient
    {
        public List<string> Calls { get; } = [];
        public Queue<HeartbeatResponse?> Heartbeats { get; } = new();
        public Queue<IReadOnlyList<CommandDto>> Polls { get; } = new();
        public List<CommandAckDto> Acked { get; } = [];
        public string? Token { get; private set; }

        public Task<EnrollOutcome> EnrollAsync(EnrollRequest request, CancellationToken ct = default)
            => Task.FromResult(EnrollOutcome.Failure(null, "not used"));

        public Task<HeartbeatResponse?> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default)
        {
            Calls.Add("heartbeat");
            return Task.FromResult(Heartbeats.Count > 0 ? Heartbeats.Dequeue() : null);
        }

        public Task<IReadOnlyList<CommandDto>> PollCommandsAsync(int waitSeconds, CancellationToken ct = default)
        {
            Calls.Add("poll");
            return Task.FromResult(Polls.Count > 0 ? Polls.Dequeue() : (IReadOnlyList<CommandDto>)[]);
        }

        public Task AckCommandsAsync(CommandAckBatch batch, CancellationToken ct = default)
        {
            Calls.Add("ack");
            Acked.AddRange(batch.Acks);
            return Task.CompletedTask;
        }

        public void UseToken(string token) => Token = token;
    }

    // ── Harness ──────────────────────────────────────────────────────────────
    private sealed class Harness
    {
        public required FakeFleetClient Client { get; init; }
        public required SessionService Session { get; init; }
        public required FleetReconciler Reconciler { get; init; }
        public required TestClock Clock { get; init; }
    }

    private static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Harness Build(FleetState? seed = null)
    {
        var clock = new TestClock(Noon);
        var ui = new Mock<IUiNotifier>();
        ui.Setup(x => x.NotifyStateChangedAsync(It.IsAny<SessionStateDto>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var tg = new Mock<ITelegramGateway>();
        tg.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var system = new Mock<ISystemController>();
        var store = new Mock<ISessionStore>(); // Load() => null

        var session = new SessionService(clock, store.Object, ui.Object, tg.Object, system.Object,
            NullLogger<SessionService>.Instance);

        var client = new FakeFleetClient();
        var reconciler = new FleetReconciler(
            client,
            new InMemoryStateStore(seed ?? new FleetState()),
            new InMemoryProcessedStore(),
            new FleetPolicyApplier(session, new FleetUpdateTarget(), NullLogger<FleetPolicyApplier>.Instance),
            new FleetDesiredApplier(session, NullLogger<FleetDesiredApplier>.Instance),
            new FleetCommandApplier(session, NullLogger<FleetCommandApplier>.Instance),
            session,
            new AgentInfo("KID-PC", "Windows 11", "2.0.11"),
            new TimeProviderClock(clock),
            NullLogger<FleetReconciler>.Instance);

        return new Harness { Client = client, Session = session, Reconciler = reconciler, Clock = clock };
    }

    private static PolicyDto Policy(int version, int play, int rest) => new()
    {
        Version = version, PlayMinutes = play, RestMinutes = rest,
        NightEnabled = false, IntervalsEnabled = true
    };

    private static CommandDto AddTime(int minutes, DateTimeOffset ttlAt) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = CommandTypes.AddTime,
        Payload = new Dictionary<string, string> { ["minutes"] = minutes.ToString() },
        TtlAt = ttlAt
    };

    // ── Scenarios ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Offline_startup_enforces_cached_policy_without_network()
    {
        // Cached policy: 60/10, intervals on. Backend is down for the whole test.
        var seed = new FleetState { Policy = Policy(2, 60, 10) };
        var h = Build(seed);

        await h.Reconciler.ApplyCachedAsync();
        h.Session.GetCurrentState().TimeRemaining.Should().Be(TimeSpan.FromMinutes(60));

        // A reconcile with the backend unreachable is a harmless no-op — cache stays in force.
        await h.Reconciler.ReconcileOnceAsync(commandWaitSeconds: 0);
        h.Session.GetCurrentState().TimeRemaining.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task Reconnect_reconciles_policy_then_desired_then_commands_in_order()
    {
        var h = Build();
        h.Client.Heartbeats.Enqueue(new HeartbeatResponse
        {
            Policy = Policy(2, 45, 15),
            Desired = new DesiredStateDto { Version = 2, Paused = true }
        });
        h.Client.Polls.Enqueue([AddTime(30, Noon.AddMinutes(5))]);

        await h.Reconciler.ReconcileOnceAsync(commandWaitSeconds: 0);

        // Policy applied (rule changed), desired applied (paused), command applied + acked.
        h.Session.IsPaused().Should().BeTrue();
        h.Client.Acked.Should().ContainSingle().Which.Ok.Should().BeTrue();

        // Order: heartbeat (policy → desired) strictly before the command poll/ack.
        h.Client.Calls.Should().Equal("heartbeat", "poll", "ack");
    }

    [Fact]
    public async Task Redelivered_command_is_applied_once()
    {
        var h = Build();
        // Fresh session starts Playing with the default 40-min rule.
        h.Session.GetCurrentState().TimeRemaining.Should().Be(TimeSpan.FromMinutes(40));

        var cmd = AddTime(30, Noon.AddMinutes(5));
        h.Client.Polls.Enqueue([cmd]); // cycle 1
        h.Client.Polls.Enqueue([cmd]); // cycle 2: same command redelivered before/around ack

        await h.Reconciler.ReconcileOnceAsync(0);
        await h.Reconciler.ReconcileOnceAsync(0);

        // +30 applied exactly once → 70, not 100.
        h.Session.GetCurrentState().TimeRemaining.Should().Be(TimeSpan.FromMinutes(70));
        h.Client.Acked.Should().OnlyContain(a => a.Id == cmd.Id && a.Ok);
    }

    [Fact]
    public async Task Expired_command_is_ignored()
    {
        var h = Build();
        var expired = AddTime(30, Noon.AddMinutes(-1)); // TTL already past
        h.Client.Polls.Enqueue([expired]);

        await h.Reconciler.DrainCommandsAsync(0);

        h.Session.GetCurrentState().TimeRemaining.Should().Be(TimeSpan.FromMinutes(40)); // untouched
        h.Client.Acked.Should().BeEmpty();       // not applied, not acked
        h.Client.Calls.Should().NotContain("ack");
    }

    [Fact]
    public async Task Commands_queued_while_offline_drain_in_order_on_reconnect()
    {
        var h = Build();

        // Offline cycle: nothing to do.
        await h.Reconciler.ReconcileOnceAsync(0);

        // Reconnect: two commands that accumulated are delivered together, applied in order.
        h.Client.Polls.Enqueue([AddTime(10, Noon.AddMinutes(5)), AddTime(5, Noon.AddMinutes(5))]);
        await h.Reconciler.ReconcileOnceAsync(0);

        // 40 + 10 + 5 = 55.
        h.Session.GetCurrentState().TimeRemaining.Should().Be(TimeSpan.FromMinutes(55));
        h.Client.Acked.Should().HaveCount(2).And.OnlyContain(a => a.Ok);
    }

    [Fact]
    public async Task Cached_pause_is_reasserted_on_startup()
    {
        var seed = new FleetState
        {
            Policy = Policy(1, 40, 20),
            Desired = new DesiredStateDto { Version = 3, Paused = true }
        };
        var h = Build(seed);

        await h.Reconciler.ApplyCachedAsync();

        h.Session.IsPaused().Should().BeTrue(); // paused survives a restart with no backend
    }
}
