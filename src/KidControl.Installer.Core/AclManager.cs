using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KidControl.Installer.Core;

/// <summary>Applies and relaxes the NTFS ACLs that protect the install and data directories.</summary>
public interface IAclManager
{
    void LockInstallDirectory(string path);

    void UnlockInstallDirectory(string path);

    void ProtectDataDirectory(string path);

    void RelaxDataDirectory(string path);
}

/// <summary>
/// Managed ACL engine for the two protected trees.
///
/// Uses <see cref="DirectorySecurity"/> with well-known SIDs rather than icacls, so
/// it is locale-independent (icacls silently mis-resolves names on non-English
/// Windows) and free of the "SYSTEM-only" ACL bug that used to lock admins out of
/// their own ProgramData tree.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AclManager : IAclManager
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private const InheritanceFlags TreeInherit =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Locks the install directory: inheritance removed, SYSTEM + Administrators get
    /// Full Control, Users keep Read+Execute so the app can still run. Because the
    /// SCM (LocalSystem) retains Full Control, the service can be registered and
    /// started AFTER this lock is applied.
    /// </summary>
    public void LockInstallDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AllowFull(security, System);
        AllowFull(security, Administrators);
        Allow(security, Users, FileSystemRights.ReadAndExecute);

        new DirectoryInfo(path).SetAccessControl(security);
    }

    /// <summary>Restores inheritance and grants Administrators Full Control so the tree can be deleted.</summary>
    public void UnlockInstallDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        AllowFull(security, Administrators);
        info.SetAccessControl(security);
    }

    /// <summary>
    /// Protects the ProgramData tree: inheritance removed, SYSTEM + Administrators
    /// Full Control. This is where appsettings.json and session_state.json live, so
    /// a standard-user child cannot delete or rewrite them.
    /// </summary>
    public void ProtectDataDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AllowFull(security, System);
        AllowFull(security, Administrators);

        new DirectoryInfo(path).SetAccessControl(security);
    }

    /// <summary>Grants Administrators + SYSTEM Full Control back so the uninstaller can remove the tree.</summary>
    public void RelaxDataDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        AllowFull(security, Administrators);
        AllowFull(security, System);
        info.SetAccessControl(security);
    }

    private static void AllowFull(DirectorySecurity security, SecurityIdentifier sid) =>
        Allow(security, sid, FileSystemRights.FullControl);

    private static void Allow(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            TreeInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
