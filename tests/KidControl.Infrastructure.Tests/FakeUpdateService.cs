using KidControl.Application.Abstractions;
using KidControl.Application.Models;

namespace KidControl.Infrastructure.Tests;

/// <summary>Recording <see cref="IUpdateService"/> double for fleet command tests.</summary>
public sealed class FakeUpdateService : IUpdateService
{
    public Version CurrentVersion { get; init; } = new(2, 0, 11);
    public string CurrentVersionText { get; init; } = "2.0.11";

    /// <summary>Set to a tag to have CheckAsync report a newer release; null → up to date.</summary>
    public string? LatestTag { get; set; }

    public string? InstalledTag { get; private set; }
    public int InstallCalls { get; private set; }

    public Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        => Task.FromResult(LatestTag is null
            ? null
            : new UpdateInfo(LatestTag, new Version(9, 9, 9), "", "setup.zip", "", 0, null));

    public Task StartInstallAsync(string tag, CancellationToken ct = default)
    {
        InstalledTag = tag;
        InstallCalls++;
        return Task.CompletedTask;
    }

    public Task StartRollbackAsync(string tag, CancellationToken ct = default)
    {
        InstalledTag = tag;
        InstallCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReleaseInfo>> GetRollbackVersionsAsync(int top, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ReleaseInfo>>([]);
}
