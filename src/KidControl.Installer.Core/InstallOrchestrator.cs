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
    /// Binary-only update: stop + kill → copy files → re-lock → re-register → start.
    /// Deliberately does NOT touch appsettings.json or session_state.json, so the
    /// operator's Telegram config and the child's current timer survive the update.
    /// </summary>
    public void Update(UpdateRequest request, Action<string> progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        progress("Stopping the service…");
        _service.StopAndWait(TimeSpan.FromSeconds(60));
        _service.Uninstall();

        progress("Terminating running KidControl processes…");
        _processes.KillAll(progress);

        progress("Unlocking install directory…");
        _acl.UnlockInstallDirectory(_locations.InstallDirectory);

        progress("Copying updated binaries…");
        _deployer.CopyBinaries(request.SourceDirectory);

        progress("Re-applying directory protection…");
        _acl.LockInstallDirectory(_locations.InstallDirectory);

        progress("Re-registering the service…");
        _service.Install(_locations.ServiceExecutablePath);
        _service.Start();

        _registry.ProtectServiceKey();
        progress("Update complete.");
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
