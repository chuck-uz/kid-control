namespace KidControl.Application.Abstractions;

/// <summary>
/// Port over OS-level side effects (power, process lifecycle). Splitting these
/// off the orchestrator lets the application layer stay free of Win32 and
/// <c>Process</c> calls, and lets tests assert intent without touching the OS.
/// </summary>
public interface ISystemController
{
    Task ShutdownAsync(TimeSpan delay, CancellationToken ct = default);
    Task RestartAsync(TimeSpan delay, CancellationToken ct = default);

    void StopUi();
    void LaunchUi();

    /// <summary>Requests the host to stop the whole service process gracefully.</summary>
    void RequestServiceStop();
}
