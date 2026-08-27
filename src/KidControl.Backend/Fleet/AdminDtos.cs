namespace KidControl.Backend.Fleet;

/// <summary>Operator request to queue a one-shot command (temporary admin API; bot in T11).</summary>
public sealed record EnqueueCommandRequest(
    string Type,
    Dictionary<string, string>? Payload = null,
    int? TtlSeconds = null);

/// <summary>Operator request to set the paused desired-override.</summary>
public sealed record PauseRequest(bool Paused);
