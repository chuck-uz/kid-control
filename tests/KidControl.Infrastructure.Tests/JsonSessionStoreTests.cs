using FluentAssertions;
using KidControl.Application.Models;
using KidControl.Domain.Enums;
using KidControl.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public sealed class JsonSessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonSessionStore _store;

    public JsonSessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "KidControlTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new JsonSessionStore(_dir, NullLogger<JsonSessionStore>.Instance);
    }

    [Fact]
    public void Load_ReturnsNull_WhenNoFileExists()
    {
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsSnapshot()
    {
        var snapshot = new SessionSnapshot
        {
            TimeRemaining = TimeSpan.FromMinutes(17),
            Status = SessionStatus.Resting,
            LastUpdated = new DateTimeOffset(2026, 8, 6, 10, 30, 0, TimeSpan.Zero),
            PlayMinutes = 55,
            RestMinutes = 12,
            NightStart = TimeSpan.FromHours(21),
            NightEnd = TimeSpan.FromHours(6)
        };

        _store.Save(snapshot);
        var loaded = _store.Load();

        loaded.Should().NotBeNull();
        loaded!.TimeRemaining.Should().Be(snapshot.TimeRemaining);
        loaded.Status.Should().Be(SessionStatus.Resting);
        loaded.LastUpdated.Should().Be(snapshot.LastUpdated);
        loaded.PlayMinutes.Should().Be(55);
        loaded.RestMinutes.Should().Be(12);
        loaded.NightStart.Should().Be(TimeSpan.FromHours(21));
        loaded.NightEnd.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileIsCorrupt()
    {
        File.WriteAllText(Path.Combine(_dir, "session_state.json"), "{ this is not valid json ]]]");

        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Load_ReturnsNull_WhenFileIsEmpty()
    {
        File.WriteAllText(Path.Combine(_dir, "session_state.json"), string.Empty);

        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Save_OverExistingFile_IsAtomicAndParses()
    {
        var first = new SessionSnapshot { TimeRemaining = TimeSpan.FromMinutes(40), Status = SessionStatus.Playing };
        _store.Save(first);

        var second = new SessionSnapshot { TimeRemaining = TimeSpan.FromMinutes(5), Status = SessionStatus.NightBlocked };
        _store.Save(second);

        var loaded = _store.Load();
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(SessionStatus.NightBlocked);
        loaded.TimeRemaining.Should().Be(TimeSpan.FromMinutes(5));

        // No leftover temp files from the atomic move.
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        Directory.GetFiles(_dir).Should().ContainSingle(f => f.EndsWith("session_state.json", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
