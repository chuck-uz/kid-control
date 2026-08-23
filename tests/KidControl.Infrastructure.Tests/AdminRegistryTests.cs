using FluentAssertions;
using KidControl.Infrastructure.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KidControl.Infrastructure.Tests;

public sealed class AdminRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AdminRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kc-admins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "admins.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private AdminRegistry New(params long[] seed) => new(seed, _path, NullLogger<AdminRegistry>.Instance);

    [Fact]
    public void Seeds_From_Config_On_First_Run()
    {
        var r = New(1, 2);
        r.IsAdmin(1).Should().BeTrue();
        r.IsAdmin(2).Should().BeTrue();
        r.IsAdmin(3).Should().BeFalse();
        r.Count.Should().Be(2);
    }

    [Fact]
    public void Add_Is_Idempotent()
    {
        var r = New(1);
        r.Add(2).Should().BeTrue();
        r.Add(2).Should().BeFalse();
        r.IsAdmin(2).Should().BeTrue();
    }

    [Fact]
    public void Never_Removes_The_Last_Admin()
    {
        var r = New(1);
        r.Remove(1).Should().BeFalse(); // last admin protected
        r.IsAdmin(1).Should().BeTrue();

        r.Add(2);
        r.Remove(1).Should().BeTrue();  // ok, 2 remains
        r.IsAdmin(1).Should().BeFalse();
    }

    [Fact]
    public void Changes_Persist_And_File_Wins_Over_Seed()
    {
        var r1 = New(1);
        r1.Add(2);

        // A new instance with a different seed must load the persisted file, not reseed.
        var r2 = New(99);
        r2.IsAdmin(1).Should().BeTrue();
        r2.IsAdmin(2).Should().BeTrue();
        r2.IsAdmin(99).Should().BeFalse();
    }
}
