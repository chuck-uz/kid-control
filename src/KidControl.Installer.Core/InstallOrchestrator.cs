using System.Runtime.Versioning;

namespace KidControl.Installer.Core;

/// <summary>
/// Sequences the single-responsibility installer components into a full install,
/// a binary-only update, and a full uninstall.
///
/// This is the central fix for the v1 God-class: all orchestration state lives here,
/// progress is reported through an injected <c>Action&lt;string&gt;</c>, and there is
/// no reference to WinForms. A silent/headless run and the GUI wizard drive the exact
/// same code path — the silent path no longer needs a hidden <c>Form</c> to exist.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallOrchestrator
{
    private readonly IServiceInstaller _service;
    private readonly IAclManager _acl;
    private readonly IProcessKiller _processes;
    private readonly IRegistryProtector _registry;
    private readonly IAppSettingsWriter _appSettings;
    private readonly IPayloadDeployer _deployer;
    private readonly InstallLocations _locations;

    public InstallOrchestrator(
        IServiceInstaller service,
        IAclManager acl,
        IProcessKiller processes,
        IRegistryProtector registry,
        IAppSettingsWriter appSettings,
        IPayloadDeployer deployer,
        InstallLocations locations)
    {
        _service = service;
        _acl = acl;
        _processes = processes;
        _registry = registry;
        _appSettings = appSettings;
        _deployer = deployer;
        _locations = locations;
    }

    /// <summary>Wires the concrete production components with a shared <see cref="InstallLocations"/>.</summary>
    public static InstallOrchestrator CreateDefault(InstallLocations? locations = null)
    {
        locations ??= new InstallLocations();
        return new InstallOrchestrator(
            new ServiceInstaller(),
            new AclManager(),
            new ProcessKiller(),
            new RegistryProtector(),
            new AppSettingsWriter(locations),
            new PayloadDeployer(locations),
            locations);
    }

    /// <summary>Full install: stop + kill → copy files → write config → lock ACLs → register service → start.</summary>
    public void Install(InstallRequest request, Action<string> progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        request.Settings.Validate();

        progress("Stopping any existing service…");
        _service.StopAndWait(TimeSpan.FromSeconds(60));
        _service.Uninstall();

        progress("Terminating running KidControl processes…");
        _processes.KillAll(progress);

        // ProgramData may carry a SYSTEM-only ACL from an old build; relax it so the
        // new config can be written before we re-protect the tree.
        _acl.RelaxDataDirectory(_locations.DataDirectory);

        progress("Copying application files…");
        _deployer.CopyBinaries(request.SourceDirectory);

        progress("Writing configuration…");
        _appSettings.Write(request.Settings); // token is never echoed to progress

        progress("Applying directory protection…");
        _acl.ProtectDataDirectory(_locations.DataDirectory);
        _acl.LockInstallDirectory(_locations.InstallDirectory);

        progress("Registering the Windows service…");
        _service.Install(_locations.ServiceExecutablePath);

        progress("Starting the service…");
        _service.Start();

        progress("Hardening registry…");
        _registry.ProtectServiceKey();
        _registry.HideFromUninstallList();

        progress("Installation complete.");
    }

    /// <summary>
    /// Crash-safe binary-only update: stop (NEVER delete) → back up current binaries → copy
    /// new files → start → health-check; roll back to the backup on any failure. Deliberately
    /// does NOT touch appsettings.json or session_state.json, so the operator's config and the
    /// child's current timer survive.
    ///
    /// The two fixes over v2.1 (which could brick the agent): (1) the service is never
    /// <c>sc delete</c>-d during an update — its registration is stable, so an interruption
    /// can't leave the machine with no service at all; (2) a failed swap is rolled back and the
    /// previous version restarted, instead of leaving half-written binaries behind.
    /// </summary>
    public void Update(UpdateRequest request, Action<string> progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        progress("Stopping the service…");
        _service.StopAndWait(TimeSpan.FromSeconds(90));

        progress("Terminating running KidControl processes…");
        _processes.KillAll(progress);

        progress("Unlocking install directory…");
        _acl.UnlockInstallDirectory(_locations.InstallDirectory);

        progress("Backing up the current version…");
        var backupDir = _deployer.BackupBinaries();

        try
        {
            progress("Copying updated binaries…");
            _deployer.CopyBinaries(request.SourceDirectory);

            progress("Re-applying directory protection…");
            _acl.LockInstallDirectory(_locations.InstallDirectory);

            // The service registration is stable across updates (fixed binPath). Only
            // (re)create it if it somehow went missing — we never delete it ourselves.
            if (!_service.IsInstalled())
            {
                progress("Registering the Windows service…");
                _service.Install(_locations.ServiceExecutablePath);
            }

            progress("Starting the service…");
            _service.Start();

            progress("Verifying the new version is healthy…");
            if (!_service.WaitUntilHealthy(TimeSpan.FromSeconds(60)))
            {
                throw new InvalidOperationException("The updated service did not stay healthy after start.");
            }

            _registry.ProtectServiceKey();
            _deployer.DiscardBackup(backupDir);
            progress("Update complete.");
        }
        catch (Exception ex)
        {
            progress($"Update failed: {ex.Message}. Rolling back to the previous version…");
            RollBack(backupDir, progress);
            throw;
        }
    }

    /// <summary>
    /// Restores the pre-update binaries and restarts the service. Best effort: any failure here
    /// is swallowed after logging via <paramref name="progress"/>, because the SCM failure-
    /// recovery (auto-restart) is the final backstop and we must not throw over the original
    /// error that triggered the rollback.
    /// </summary>
    private void RollBack(string backupDir, Action<string> progress)
    {
        try
        {
            _service.StopAndWait(TimeSpan.FromSeconds(60));
            _processes.KillAll(progress);
            _acl.UnlockInstallDirectory(_locations.InstallDirectory);
            _deployer.RestoreBinaries(backupDir);
            _acl.LockInstallDirectory(_locations.InstallDirectory);

            if (!_service.IsInstalled())
            {
                _service.Install(_locations.ServiceExecutablePath);
            }

            _service.Start();
            _registry.ProtectServiceKey();
            progress("Rolled back to the previous version.");
        }
        catch (Exception ex)
        {
            progress($"Rollback also failed: {ex.Message}. SCM auto-restart will recover the service.");
        }
    }

    /// <summary>Full uninstall: unprotect → stop + delete service → kill → relax ACLs → delete trees.</summary>
    public void Uninstall(Action<string> progress, bool removeData = true)
    {
        ArgumentNullException.ThrowIfNull(progress);

        progress("Removing registry hardening…");
        _registry.UnprotectServiceKey();
        _registry.RemoveFromUninstallList();

        progress("Stopping and deleting the service…");
        _service.StopAndWait(TimeSpan.FromSeconds(60));
        _service.Uninstall();

        progress("Terminating running KidControl processes…");
        _processes.KillAll(progress);

        progress("Relaxing directory permissions…");
        _acl.UnlockInstallDirectory(_locations.InstallDirectory);
        _acl.RelaxDataDirectory(_locations.DataDirectory);

        progress("Deleting application files…");
        _deployer.DeleteDirectory(_locations.InstallDirectory, progress);

        if (removeData)
        {
            progress("Deleting application data…");
            _deployer.DeleteDirectory(_locations.DataDirectory, progress);
        }

        progress("Uninstall complete.");
    }
}
