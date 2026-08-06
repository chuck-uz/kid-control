using System.Globalization;
using System.Text.Json;

namespace KidControl.Installer.Core;

/// <summary>Writes the runtime appsettings.json into the protected ProgramData tree.</summary>
public interface IAppSettingsWriter
{
    /// <summary>Serializes <paramref name="settings"/> to appsettings.json. Returns the file path.</summary>
    string Write(InstallSettings settings);
}

/// <summary>
/// Emits the runtime <c>appsettings.json</c> that the ServiceHost loads from
/// <c>%ProgramData%\KidControl</c> (its config override location). The section shape
/// matches the strongly-typed config the host binds:
/// <c>Telegram</c> / <c>Update</c> / <c>Protection</c> / <c>Serilog</c>.
///
/// The bot token is written to the protected file but is NEVER written to a log,
/// the console, or the progress callback.
/// </summary>
public sealed class AppSettingsWriter : IAppSettingsWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly InstallLocations _locations;

    public AppSettingsWriter(InstallLocations? locations = null) =>
        _locations = locations ?? new InstallLocations();

    public string Write(InstallSettings settings)
    {
        settings.Validate();

        Directory.CreateDirectory(_locations.DataDirectory);
        Directory.CreateDirectory(_locations.LogsDirectory);

        var textLog = Path.Combine(_locations.LogsDirectory, "kidcontrol-.log");
        var jsonLog = Path.Combine(_locations.LogsDirectory, "kidcontrol-ai-.json");

        // A dictionary (not an anonymous type) so the section keys are exactly the
        // SectionName constants the host expects, independent of C# property casing.
        var model = new Dictionary<string, object?>
        {
            ["Telegram"] = new Dictionary<string, object?>
            {
                ["BotToken"] = settings.BotToken,
                ["AdminChatIds"] = settings.AdminChatIds.ToArray(),
                // TimeSpan bind-from-string; format explicitly to avoid serializer ambiguity.
                ["NightStart"] = Format(settings.NightStart),
                ["NightEnd"] = Format(settings.NightEnd),
            },
            ["Update"] = new Dictionary<string, object?>
            {
                ["Enabled"] = true,
                ["Owner"] = settings.UpdateOwner,
                ["Repository"] = settings.UpdateRepository,
                ["RequireSignature"] = settings.RequireSignature,
                ["TrustedThumbprint"] = settings.TrustedThumbprint,
                ["CheckInterval"] = Format(TimeSpan.FromHours(6)),
            },
            ["Protection"] = new Dictionary<string, object?>
            {
                ["CriticalProcess"] = settings.CriticalProcess,
                ["ApplyProcessDacl"] = settings.ApplyProcessDacl,
                ["TamperDetection"] = settings.TamperDetection,
            },
            ["Serilog"] = BuildSerilog(textLog, jsonLog),
        };

        var json = JsonSerializer.Serialize(model, Options);
        File.WriteAllText(_locations.AppSettingsPath, json);
        return _locations.AppSettingsPath;
    }

    private static Dictionary<string, object?> BuildSerilog(string textLog, string jsonLog) => new()
    {
        ["Using"] = new[] { "Serilog.Sinks.Console", "Serilog.Sinks.File", "Serilog.Formatting.Compact" },
        ["MinimumLevel"] = new Dictionary<string, object?>
        {
            ["Default"] = "Information",
            ["Override"] = new Dictionary<string, object?>
            {
                ["Microsoft"] = "Warning",
                ["System"] = "Warning",
            },
        },
        ["WriteTo"] = new object[]
        {
            new Dictionary<string, object?> { ["Name"] = "Console" },
            new Dictionary<string, object?>
            {
                ["Name"] = "File",
                ["Args"] = new Dictionary<string, object?>
                {
                    ["path"] = textLog,
                    ["rollingInterval"] = "Day",
                    ["retainedFileCountLimit"] = 14,
                    ["shared"] = true,
                },
            },
            new Dictionary<string, object?>
            {
                ["Name"] = "File",
                ["Args"] = new Dictionary<string, object?>
                {
                    ["path"] = jsonLog,
                    ["rollingInterval"] = "Day",
                    ["retainedFileCountLimit"] = 14,
                    ["shared"] = true,
                    ["formatter"] = "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
                },
            },
        },
        ["Enrich"] = new[] { "FromLogContext" },
    };

    private static string Format(TimeSpan value) =>
        value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
