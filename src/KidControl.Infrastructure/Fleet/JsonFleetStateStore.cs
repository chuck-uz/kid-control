using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Persists <see cref="FleetState"/> as JSON at <c>%ProgramData%\KidControl\fleet_state.json</c>
/// (the DACL-protected app-data root the child can't touch). Writes are atomic (temp + replace);
/// an unreadable file degrades to an empty state so a corrupt cache can't wedge the agent.
/// </summary>
public sealed class JsonFleetStateStore(ILogger<JsonFleetStateStore> logger) : IFleetStateStore
{
    private static string FilePath => Path.Combine(AppPaths.Root, "fleet_state.json");

    public FleetState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new FleetState();

            var json = File.ReadAllText(FilePath);
            return FleetJson.Deserialize<FleetState>(json) ?? new FleetState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Could not read fleet state; starting from empty.");
            return new FleetState();
        }
    }

    public void Save(FleetState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var json = FleetJson.Serialize(state);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not persist fleet state.");
        }
    }
}
