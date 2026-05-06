using KidControl.Application.Models;

namespace KidControl.Application.Interfaces;

/// <summary>
/// Orchestrates update lifecycle: check, download, install, rollback.
/// Implementation lives in Infrastructure (uses GitHubReleaseClient + Process.Start).
/// </summary>
public interface IUpdateService
{
    /// <summary>Returns current product version (from assembly).</summary>
    Version GetCurrentVersion();

    /// <summary>Checks GitHub for a release with version greater than current.</summary>
    Task<UpdateInfoDto?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the installer asset for the given tag, writes the post-update marker,
    /// spawns the installer with /silent /update and signals graceful host shutdown.
    /// </summary>
    Task StartInstallAsync(string tag, CancellationToken ct = default);

    /// <summary>Returns recent releases (excluding the current version) for rollback selection.</summary>
    Task<IReadOnlyList<ReleaseDto>> GetAvailableVersionsForRollbackAsync(int top = 10, CancellationToken ct = default);

    /// <summary>Same flow as StartInstallAsync but uses the chosen tag's asset and marks marker as 'rollback'.</summary>
    Task StartRollbackAsync(string tag, CancellationToken ct = default);
}
