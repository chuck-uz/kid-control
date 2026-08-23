using KidControl.Application.Models;

namespace KidControl.Application.Abstractions;

/// <summary>Port: self-update against a signed release feed.</summary>
public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>Human-readable running version, incl. any pre-release label (e.g. "2.0.2" or "0.0.1-source").</summary>
    string CurrentVersionText { get; }

    /// <summary>Returns update info if a newer, verified release is available; otherwise null.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);

    /// <summary>Downloads, verifies (signature + hash), and launches the installer for the given tag.</summary>
    Task StartInstallAsync(string tag, CancellationToken ct = default);

    Task StartRollbackAsync(string tag, CancellationToken ct = default);

    Task<IReadOnlyList<ReleaseInfo>> GetRollbackVersionsAsync(int top, CancellationToken ct = default);
}
