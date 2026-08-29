using System.Text.Json;
using System.Text.Json.Nodes;

namespace KidControl.Installer.Core;

/// <summary>
/// Read-modify-writes the <c>Fleet</c> section of the installed <c>appsettings.json</c>
/// (managed mode: backend URL + one-time enroll code) while preserving every other section.
/// Used by the post-install enrollment GUI so an operator can bind the device without editing
/// JSON by hand.
/// </summary>
public sealed class FleetConfigWriter(InstallLocations? locations = null)
{
    private readonly InstallLocations _loc = locations ?? new InstallLocations();

    public const string DefaultBackendUrl = "https://kidcontrol.oresh.in";

    /// <summary>Current Fleet:Url from appsettings, or the default when unset.</summary>
    public string ReadBackendUrl()
    {
        var root = Load();
        var url = (root["Fleet"] as JsonObject)?["Url"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(url) ? DefaultBackendUrl : url;
    }

    /// <summary>Write Fleet:Url + Fleet:EnrollCode, preserving all other config. Returns the path.</summary>
    public string Write(string backendUrl, string enrollCode)
    {
        var root = Load();
        if (root["Fleet"] is not JsonObject fleet)
        {
            fleet = new JsonObject();
            root["Fleet"] = fleet;
        }
        fleet["Url"] = backendUrl.Trim();
        fleet["EnrollCode"] = enrollCode.Trim();

        Directory.CreateDirectory(_loc.DataDirectory);
        File.WriteAllText(_loc.AppSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return _loc.AppSettingsPath;
    }

    /// <summary>True once the agent has enrolled (its encrypted identity file exists).</summary>
    public bool IsEnrolled() => File.Exists(Path.Combine(_loc.DataDirectory, "device_identity.dat"));

    private JsonObject Load()
    {
        if (File.Exists(_loc.AppSettingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(_loc.AppSettingsPath)) is JsonObject obj)
                    return obj;
            }
            catch (JsonException) { /* fall through to a fresh object */ }
        }
        return new JsonObject();
    }
}
