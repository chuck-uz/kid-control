using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Remembers command ids the agent has already applied, so a redelivered command (delivery is
/// at-least-once) is never applied twice — apply-once by id (§7). Bounded to the most recent
/// ids; older ones age out (by then the backend has the ack and won't resend). Durable so a
/// restart mid-ack can't reapply. Not secret.
/// </summary>
public interface IProcessedCommandStore
{
    bool Contains(string commandId);
    void Add(string commandId);
    void Save();
}

public sealed class JsonProcessedCommandStore : IProcessedCommandStore
{
    private const int MaxIds = 200;
    private readonly ILogger<JsonProcessedCommandStore> _logger;
    private readonly List<string> _ids; // insertion order; oldest first
    private readonly HashSet<string> _set;

    private static string FilePath => Path.Combine(AppPaths.Root, "fleet_commands.json");

    public JsonProcessedCommandStore(ILogger<JsonProcessedCommandStore> logger)
    {
        _logger = logger;
        _ids = Load(logger);
        _set = [.. _ids];
    }

    public bool Contains(string commandId) => _set.Contains(commandId);

    public void Add(string commandId)
    {
        if (!_set.Add(commandId))
            return;
        _ids.Add(commandId);
        while (_ids.Count > MaxIds)
        {
            _set.Remove(_ids[0]);
            _ids.RemoveAt(0);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var json = FleetJson.Serialize(_ids);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist processed-command ids.");
        }
    }

    private static List<string> Load(ILogger logger)
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            return FleetJson.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not read processed-command ids; starting empty.");
            return [];
        }
    }
}
