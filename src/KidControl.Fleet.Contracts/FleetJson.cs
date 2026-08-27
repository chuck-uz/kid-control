using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidControl.Fleet.Contracts;

/// <summary>Shared JSON settings for all fleet wire traffic (agent ⇄ backend).</summary>
public static class FleetJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // Tolerant reads: ignore unknown members and case, don't emit nulls.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
