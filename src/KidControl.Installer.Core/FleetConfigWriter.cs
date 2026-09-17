using System.Reflection;
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

    /// <summary>
    /// Backend URL baked in at build time (MSBuild property <c>KcBackendUrl</c>, from the untracked
    /// <c>Directory.Build.local.props</c> or the <c>KC_BACKEND_URL</c> env var). Empty when the
    /// build set none -- then the URL must be supplied explicitly.
    /// </summary>
    public static string DefaultBackendUrl { get; } =
        typeof(FleetConfigWriter).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "KcBackendUrl")?.Value?.Trim() ?? string.Empty;

    /// <summary>Current Fleet:Url from appsettings, or the build-time default (possibly empty) when unset.</summary>
    public string ReadBackendUrl()
    {
        var root = Load();
        var url = (root["Fleet"] as JsonObject)?["Url"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(url) ? DefaultBackendUrl : url;
    }

    /// <summary>Write Fleet:Url + Fleet:EnrollCode, preserving all other config. Returns the path.</summary>
    public string Write(string backendUrl, string enrollCode)
    {
        if (string.IsNullOrWhiteSpace(backendUrl))
            throw new ArgumentException(
                "Backend URL is required: no default was set at build time (KcBackendUrl / KC_BACKEND_URL).",
                nameof(backendUrl));

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
