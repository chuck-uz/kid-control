using System.Diagnostics;
using System.Runtime.Versioning;
using KidControl.Contracts;

namespace KidControl.Installer.Core;

/// <summary>Terminates running KidControl processes that would lock the install directory.</summary>
public interface IProcessKiller
{
    /// <summary>Kill all KidControl processes. Returns how many were terminated. Best effort.</summary>
    int KillAll(Action<string>? progress = null);
}

/// <summary>
/// Terminates the KidControl service/UI processes so their executables can be
/// overwritten or deleted.
///
/// Deliberately plain: <see cref="Process.Kill(bool)"/> with the whole tree. No
/// base64-encoded PowerShell, no CIM sweeps, no P/Invoke SeDebugPrivilege games —
/// the v1 code that did all that was both a maintenance nightmare and an
/// AV/EDR red flag. The installer already runs elevated; that is enough to kill
/// LocalSystem-owned processes it created.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessKiller : IProcessKiller
{
    private static readonly string[] ProcessNames =
    {
        KidControlNames.ServiceProcessName,
        KidControlNames.UiProcessName,
    };

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    public int KillAll(Action<string>? progress = null)
    {
        var killed = 0;
        var self = Environment.ProcessId;

        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == self)
                    {
                        continue;
                    }

                    try
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit((int)ExitWait.TotalMilliseconds);
                        killed++;
                        progress?.Invoke($"Terminated {name} (pid {process.Id}).");
                    }
                    catch (Exception ex)
                    {
                        // Already exiting, access denied, or a race — best effort only.
                        progress?.Invoke($"Could not terminate {name} (pid {process.Id}): {ex.Message}");
                    }
                }
            }
        }

        return killed;
    }
}
