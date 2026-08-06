namespace KidControl.Contracts;

/// <summary>
/// Line-based protocol for the Admin-only command pipe. Kept tiny and explicit so
/// both the service and the unlock tool agree on exact verbs and responses.
/// </summary>
public static class CommandPipeProtocol
{
    // Requests (tool -> service)
    public const string InitiateEmergencyAuth = "INITIATE_EMERGENCY_AUTH";
    public const string EmergencyShutdownPrefix = "EMERGENCY_SHUTDOWN:";

    // Responses (service -> tool)
    public const string Ok = "OK";
    public const string Success = "SUCCESS";
    public const string Denied = "DENIED";
    public const string RateLimited = "RATE_LIMITED";
    public const string BadRequest = "BAD_REQUEST";
}
