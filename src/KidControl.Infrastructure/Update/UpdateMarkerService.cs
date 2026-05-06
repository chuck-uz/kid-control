using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Manages C:\ProgramData\KidControl\update_pending.json — a marker file written by the
/// outgoing ServiceHost just before launching the silent installer, and consumed by the
/// freshly started ServiceHost so it can post a Telegram "update completed" notification.
/// </summary>
public sealed class UpdateMarkerService
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidControl",
        "update_pending.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool MarkerExists() => File.Exists(MarkerPath);

    public void Write(UpdateMarker marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        var json = JsonSerializer.Serialize(marker, JsonOptions);
        File.WriteAllText(MarkerPath, json);
    }

    public UpdateMarker? TryRead()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var json = File.ReadAllText(MarkerPath);
            return JsonSerializer.Deserialize<UpdateMarker>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch { /* best effort */ }
    }
}

public sealed class UpdateMarker
{
    /// <summary>"update" or "rollback".</summary>
    public string Kind { get; set; } = "update";

    public string TargetTag { get; set; } = string.Empty;

    public long[] AdminChatIds { get; set; } = Array.Empty<long>();

    public DateTimeOffset StartedUtc { get; set; }

    public string? PreviousVersion { get; set; }
}
