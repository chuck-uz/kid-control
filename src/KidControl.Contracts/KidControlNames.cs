namespace KidControl.Contracts;

/// <summary>
/// Single source of truth for cross-process identifiers. The original code
/// scattered these as literals across a dozen files — and baked a version number
/// into the Windows service name ("KidControlv0.4"), which orphaned the service
/// on every release. Here the service identity is version-independent.
/// </summary>
public static class KidControlNames
{
    /// <summary>Windows service name. Deliberately version-free.</summary>
    public const string ServiceName = "KidControlService";

    public const string ServiceDisplayName = "KidControl Parental Time Control";

    /// <summary>Pipe: service -> UI, pushes session state (state broadcast).</summary>
    public const string StatePipe = "KidControl.State";

    /// <summary>Pipe: privileged tools -> service, emergency commands (Admin-only ACL).</summary>
    public const string CommandPipe = "KidControl.Command";

    /// <summary>Pipe: service -> UI, screenshot requests.</summary>
    public const string UiCommandPipe = "KidControl.UiCommand";

    public const string ServiceProcessName = "KidControl.ServiceHost";
    public const string UiProcessName = "KidControl.UiHost";
    public const string UiExecutableName = "KidControl.UiHost.exe";

    /// <summary>Scheduled task that launches the UI in the interactive session (Session 0 fallback).</summary>
    public const string UiLaunchTaskName = "KidControl.UiHost.Launch";

    /// <summary>
    /// One-shot scheduled task that applies a staged self-update. Run under Task Scheduler
    /// (SYSTEM), it is deliberately NOT a child of the service it swaps — so stopping the
    /// service can never kill the updater mid-swap (the v2.1 brick).
    /// </summary>
    public const string UpdateTaskName = "KidControl.Update.Apply";

    /// <summary>Root of protected app data, under %ProgramData%.</summary>
    public const string AppDataFolderName = "KidControl";
}
