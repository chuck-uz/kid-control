namespace KidControl.Infrastructure.Update;

/// <summary>
/// Launches the installer that swaps the agent binaries, in a process that is DETACHED from
/// the service. This is the heart of the crash-safe self-update: the swap must never run as a
/// child of the very service it stops, or stopping the service kills the swap mid-flight and
/// leaves the machine with half-written (or deleted) binaries — the v2.1 "app stops responding"
/// brick. The Windows implementation hands the job to Task Scheduler (SYSTEM), which runs it
/// outside the service's process tree.
/// </summary>
public interface IUpdateLauncher
{
    /// <summary>
    /// Start <paramref name="installerExe"/> in <c>/apply-update</c> mode against
    /// <paramref name="sourceDir"/>, detached from this process. Returns once the updater has
    /// been launched; the updater is then responsible for stopping the service, swapping the
    /// binaries, restarting, health-checking and rolling back on failure. Throws if the updater
    /// could not be launched (the caller then leaves the running version untouched).
    /// </summary>
    void LaunchDetached(string installerExe, string sourceDir);
}
