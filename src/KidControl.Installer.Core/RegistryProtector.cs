using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using KidControl.Contracts;
using Microsoft.Win32;

namespace KidControl.Installer.Core;

/// <summary>Optional registry hardening: hide from Add/Remove and protect the service key.</summary>
public interface IRegistryProtector
{
    void HideFromUninstallList();

    void RemoveFromUninstallList();

    void ProtectServiceKey();

    void UnprotectServiceKey();
}

/// <summary>
/// Optional registry-level hardening. Every method is best effort and self-contained:
/// a failure here degrades hardening but must never abort an install or uninstall.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryProtector : IRegistryProtector
{
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KidControl";

    private readonly string _serviceName;

    public RegistryProtector(string? serviceName = null) =>
        _serviceName = serviceName ?? KidControlNames.ServiceName;

    private string ServiceKeyPath => $@"SYSTEM\CurrentControlSet\Services\{_serviceName}";

    /// <summary>
    /// Hides the app from Settings ▸ Apps and Control Panel ▸ Programs.
    /// SystemComponent=1 hides it from modern Settings; NoRemove/NoModify disable the
    /// classic Control Panel buttons.
    /// </summary>
    public void HideFromUninstallList()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(UninstallKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            key.SetValue("DisplayName", "KidControl", RegistryValueKind.String);
            key.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
            key.SetValue("NoRemove", 1, RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        }
        catch
        {
            // Hardening is optional — ignore.
        }
    }

    public void RemoveFromUninstallList()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKey(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // Optional — ignore.
        }
    }

    /// <summary>
    /// Restricts the service registry key so only SYSTEM may modify the registration;
    /// Administrators retain read access. Prevents a non-SYSTEM admin child from
    /// deleting the service out from under the SCM.
    /// </summary>
    public void ProtectServiceKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            var acl = key.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            acl.AddAccessRule(new RegistryAccessRule(
                system,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            acl.AddAccessRule(new RegistryAccessRule(
                admins,
                RegistryRights.ReadKey,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            key.SetAccessControl(acl);
        }
        catch
        {
            // Optional — ignore.
        }
    }

    /// <summary>Restores inherited ACLs so sc.exe can delete the key during uninstall.</summary>
    public void UnprotectServiceKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            var acl = key.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.AddAccessRule(new RegistryAccessRule(
                admins,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            key.SetAccessControl(acl);
        }
        catch
        {
            // Best effort — sc.exe delete may still succeed under the SYSTEM context.
        }
    }
}
