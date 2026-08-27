namespace KidControl.Fleet.Contracts;

/// <summary>Agent → backend: exchange a one-time enrollment code for a device token.</summary>
public sealed record EnrollRequest(
    string Code,
    string MachineName,
    string? OsInfo = null,
    string? AgentVersion = null);

/// <summary>Backend → agent: the identity the agent uses for all later calls.</summary>
public sealed record EnrollResponse(
    string DeviceId,
    string Token);
