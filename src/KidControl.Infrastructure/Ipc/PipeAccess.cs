using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KidControl.Infrastructure.Ipc;

/// <summary>
/// Builds <see cref="PipeSecurity"/> descriptors for the app's named pipes.
///
/// The original design granted <c>Authenticated Users</c> read/write on every pipe,
/// which let any logged-in process drive the Admin-only command channel. Here the
/// command pipe is locked to SYSTEM + Administrators, and the state pipe additionally
/// grants the interactive user read-only so the UI can receive state pushes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PipeAccess
{
    /// <summary>SYSTEM + Administrators FullControl only — for the privileged command pipe.</summary>
    public static PipeSecurity CreateAdminOnly()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return security;
    }

    /// <summary>
    /// Admin-only, plus the interactive user gets READ so the UI process (running in the
    /// user session) can consume state broadcasts without being able to write.
    /// </summary>
    public static PipeSecurity CreateStatePipe()
    {
        var security = CreateAdminOnly();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
