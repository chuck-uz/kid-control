namespace KidControl.Fleet.Contracts;

/// <summary>Known one-shot command types (imperative, TTL'd, at-most-once, acked).</summary>
public static class CommandTypes
{
    public const string AddTime = "add_time";      // payload: { "minutes": "30" }
    public const string ResetTimer = "reset_timer";
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";
    public const string UpdateNow = "update_now";  // payload: { "tag": "v2.0.10" } (optional)

    // Phase 2 (media relay) — declared here so both sides agree on the verb.
    public const string Screenshot = "screenshot"; // payload: { "uploadId": "..." }
    public const string PlayAudio = "play_audio";   // payload: { "url": "..." }
}

/// <summary>A single queued command delivered to the agent.</summary>
public sealed record CommandDto
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;

    /// <summary>Optional string→string arguments (e.g. minutes, tag, url).</summary>
    public IReadOnlyDictionary<string, string>? Payload { get; init; }

    /// <summary>After this moment the command is stale and must be dropped, not run.</summary>
    public DateTimeOffset TtlAt { get; init; }

    public bool IsExpired(DateTimeOffset now) => now > TtlAt;

    public int? GetInt(string key)
        => Payload is not null && Payload.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;

    public string? GetString(string key)
        => Payload is not null && Payload.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Agent → backend: result of executing one command (at-most-once).</summary>
public sealed record CommandAckDto(string Id, bool Ok, string? Error = null);

/// <summary>Agent → backend: a batch of acks.</summary>
public sealed record CommandAckBatch(IReadOnlyList<CommandAckDto> Acks);
