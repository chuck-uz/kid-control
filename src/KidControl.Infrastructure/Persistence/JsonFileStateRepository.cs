using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KidControl.Application.Interfaces;
using KidControl.Application.Models;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Persistence;

[SupportedOSPlatform("windows10.0")]
public sealed class JsonFileStateRepository(ILogger<JsonFileStateRepository> logger) : ISessionStateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _basePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidControl");

    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidControl",
        "session_state.json");

    public SessionPersistenceState? Load()
    {
        try
        {
            EnsureDirectorySecurity();
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var raw = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SessionPersistenceState>(raw, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load session state from {Path}", _filePath);
            return null;
        }
    }

    public void Save(SessionPersistenceState state)
    {
        try
        {
            EnsureDirectorySecurity();
            var payload = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(_filePath, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save session state to {Path}", _filePath);
        }
    }

    private void EnsureDirectorySecurity()
    {
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            adminsSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        new DirectoryInfo(_basePath).SetAccessControl(security);
    }
}
