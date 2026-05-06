namespace KidControl.Infrastructure.Configuration;

public sealed class ProtectionConfig
{
    public const string SectionName = "Protection";

    /// <summary>
    /// When true, marks the service process as a Windows critical process via
    /// RtlSetProcessIsCritical. Any forced termination of the process (e.g. via
    /// Task Manager or Process Hacker) will trigger an immediate system reboot.
    /// WARNING: an unhandled exception or crash inside the service will also
    /// trigger a reboot. Enable only after thorough stability testing.
    /// </summary>
    public bool CriticalProcess { get; set; } = false;
}
