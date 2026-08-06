namespace KidControl.Infrastructure.Configuration;

public sealed class ProtectionConfig
{
    public const string SectionName = "Protection";

    /// <summary>
    /// Marks the service process as critical (killing it BSODs the machine). Off by default:
    /// it is a foot-gun (any unhandled crash blue-screens the box) and must be an explicit opt-in.
    /// When enabled, the host wraps it in try/finally + ProcessExit so a crash still clears the flag.
    /// </summary>
    public bool CriticalProcess { get; init; }

    /// <summary>Applies a DACL denying PROCESS_TERMINATE to non-SYSTEM principals.</summary>
    public bool ApplyProcessDacl { get; init; } = true;

    /// <summary>Enables the file-system tamper watcher over the install directory.</summary>
    public bool TamperDetection { get; init; } = true;
}
