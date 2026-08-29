using System.Runtime.Versioning;
using System.ServiceProcess;
using KidControl.Contracts;

namespace KidControl.Installer.Core;

/// <summary>Manages the lifecycle of the KidControl Windows service.</summary>
[SupportedOSPlatform("windows")]
public interface IServiceInstaller
{
    bool IsInstalled();

    void Install(string exePath);

    void Uninstall();

    void Start();

    void StopAndWait(TimeSpan timeout);

    /// <summary>
    /// Returns true if the service reaches Running within <paramref name="timeout"/> AND stays
    /// Running through a short stability window — so a new build that crash-loops (flaps
    /// Running→Stopped) is reported unhealthy and the update path can roll back.
    /// </summary>
    bool WaitUntilHealthy(TimeSpan timeout);
}

/// <summary>
/// Installs / removes / starts / stops the KidControl Windows service.
///
/// One responsibility: service lifecycle. Creation and deletion go through sc.exe
/// (there is no managed API to create a service), start/stop/query use the managed
/// <see cref="ServiceController"/>. The service identity is the version-free
/// <see cref="KidControlNames.ServiceName"/> — the v1 bug of baking a version into
/// the name (orphaning the service on every release) is gone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceInstaller : IServiceInstaller
{
    private static readonly TimeSpan ScTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(8);

    private readonly string _serviceName;
    private readonly string _displayName;

    public ServiceInstaller(string? serviceName = null, string? displayName = null)
    {
        _serviceName = serviceName ?? KidControlNames.ServiceName;
        _displayName = displayName ?? KidControlNames.ServiceDisplayName;
    }

    public bool IsInstalled()
    {
        var services = ServiceController.GetServices();
        try
        {
            return Array.Exists(
                services,
                s => string.Equals(s.ServiceName, _serviceName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }

    public void Install(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("Service executable path is required.", nameof(exePath));
        }

        // sc.exe syntax quirk: each "key= value" pair needs a space AFTER the '='.
        // The binPath is quoted so a path containing spaces is stored intact.
        var create = ProcessRunner.Run(
            "sc.exe",
            $"create {_serviceName} binPath= \"{exePath}\" start= auto obj= LocalSystem DisplayName= \"{_displayName}\"",
            ScTimeout);

        if (!create.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to create service '{_serviceName}' (exit {create.ExitCode}). {create.Detail}");
        }

        // Auto-restart on crash. Best effort: a missing recovery config must not fail install.
        ProcessRunner.Run(
            "sc.exe",
            $"failure {_serviceName} reset= 0 actions= restart/1000/restart/1000/restart/1000",
            ScTimeout);
    }

    public void Uninstall()
    {
        if (!IsInstalled())
        {
            return;
        }

        StopAndWait(TimeSpan.FromSeconds(60));

        var delete = ProcessRunner.Run("sc.exe", $"delete {_serviceName}", ScTimeout);
        if (!delete.Succeeded && IsInstalled())
        {
            throw new InvalidOperationException(
                $"Failed to delete service '{_serviceName}' (exit {delete.ExitCode}). {delete.Detail}");
        }
    }

    public void Start()
    {
        using var controller = new ServiceController(_serviceName);
        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public bool WaitUntilHealthy(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var controller = new ServiceController(_serviceName);

        // Phase 1: reach Running (SCM may take a moment; a crash also triggers failure-recovery
        // restarts, so a transient Stopped isn't yet a verdict — keep polling until the deadline).
        while (DateTime.UtcNow < deadline)
        {
            controller.Refresh();
            if (controller.Status == ServiceControllerStatus.Running)
            {
                break;
            }

            Thread.Sleep(500);
        }

        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Running)
        {
            return false;
        }

        // Phase 2: stability window — the new binaries must stay up, not flap back to Stopped.
        var stableDeadline = DateTime.UtcNow + StabilityWindow;
        while (DateTime.UtcNow < stableDeadline)
        {
            Thread.Sleep(1000);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Running)
            {
                return false;
            }
        }

        return true;
    }

    public void StopAndWait(TimeSpan timeout)
    {
        if (!IsInstalled())
        {
            return;
        }

        using var controller = new ServiceController(_serviceName);
        controller.Refresh();

        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        try
        {
            if (controller.CanStop)
            {
                controller.Stop();
            }

            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            // Caller (orchestrator) will fall back to ProcessKiller to break the lock.
        }
        catch (InvalidOperationException)
        {
            // Service vanished between the check and the stop — treat as stopped.
        }
    }
}
