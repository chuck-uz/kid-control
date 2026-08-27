namespace KidControl.Fleet.Contracts;

/// <summary>
/// Agent → backend, periodic. Reports live status and the versions the agent currently
/// holds, so the backend can answer with a fresh policy/desired snapshot only when needed.
/// </summary>
public sealed record HeartbeatRequest
{
    public StatusReportDto Status { get; init; } = new();
    public int PolicyVersion { get; init; }
    public int DesiredVersion { get; init; }
}

/// <summary>
/// Backend → agent. <see cref="Policy"/>/<see cref="Desired"/> are non-null only when the
/// agent's version was stale (the agent then applies + caches them). <see cref="HasCommands"/>
/// hints the agent to issue a long-poll for pending commands.
/// </summary>
public sealed record HeartbeatResponse
{
    public PolicyDto? Policy { get; init; }
    public DesiredStateDto? Desired { get; init; }
    public bool HasCommands { get; init; }
}
