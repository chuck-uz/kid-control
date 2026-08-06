using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KidControl.Application.Abstractions;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("KidControl.Infrastructure.Tests")]

namespace KidControl.Infrastructure.Persistence;

/// <summary>
/// Durable <see cref="ISessionStore"/> backed by a single JSON file under protected
/// %ProgramData%.
///
/// Hardening over the original repository:
///  * <b>Atomic writes</b> — the snapshot is written to a sibling temp file and then
///    moved over the target, so a crash mid-write never leaves a truncated file that
///    <see cref="Load"/> would treat as corrupt (which silently reset the timer).
///  * <b>Tolerant load</b> — a missing or unparsable file yields <c>null</c> instead of
///    throwing, letting the caller start from defaults.
///  * <b>Restrictive ACL</b> applied to the data root exactly once, guarded so the class
///    still runs on non-Windows CI (the ACL is a no-op there).
/// </summary>
public sealed class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly string _filePath;
    private readonly ILogger<JsonSessionStore> _logger;
    private readonly object _sync = new();
    private bool _aclApplied;

    public JsonSessionStore(ILogger<JsonSessionStore> logger)
        : this(AppPaths.Root, logger)
    {
    }

    /// <summary>Test/override seam: persists under an arbitrary directory.</summary>
    internal JsonSessionStore(string directory, ILogger<JsonSessionStore> logger)
    {
        _directory = directory;
        _filePath = Path.Combine(directory, "session_state.json");
        _logger = logger;
    }

    public SessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var raw = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SessionSnapshot>(raw, JsonOptions);
        }
        catch (Exception ex)
        {
            // Corrupt / partially-written / unreadable file: fall back to defaults, never throw.
            _logger.LogWarning(ex, "Failed to load session snapshot from {Path}; ignoring.", _filePath);
            return null;
        }
    }

    public void Save(SessionSnapshot snapshot)
    {
        try
        {
            EnsureDirectory();

            var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tempPath = Path.Combine(_directory, $".session_state.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(tempPath, payload);
            try
            {
                // Atomic replace on the same volume; never a partial write over the live file.
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist session snapshot to {Path}.", _filePath);
        }
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_directory))
        {
            Directory.CreateDirectory(_directory);
        }

        if (_aclApplied)
        {
            return;
        }

        lock (_sync)
        {
            if (_aclApplied)
            {
                return;
            }

            // Guarded so the store is a no-op ACL-wise off Windows (lets tests run on any OS).
            if (OperatingSystem.IsWindows())
            {
                TryApplyRestrictiveAcl();
            }

            _aclApplied = true;
        }
    }

    [SupportedOSPlatform("windows")]
    private void TryApplyRestrictiveAcl()
    {
        try
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[] { systemSid, adminsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            new DirectoryInfo(_directory).SetAccessControl(security);
            _logger.LogInformation("Applied restrictive ACL to {Path}.", _directory);
        }
        catch (Exception ex)
        {
            // Best-effort: defence-in-depth, not a hard dependency for the app to run.
            _logger.LogWarning(ex, "Failed to apply restrictive ACL to {Path}.", _directory);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file {Path}.", path);
        }
    }
}
