using System.Text.Json;
using KidControl.Application.Abstractions;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Telegram;

/// <summary>
/// Runtime-editable, persisted set of administrator chat ids. On first run it is seeded
/// from <see cref="TelegramConfig.AdminChatIds"/> (the installer-configured admins) and
/// written to <c>%ProgramData%\KidControl\admins.json</c>; after that the file is the
/// source of truth, so admins added/removed via the bot survive restarts.
/// </summary>
public sealed class AdminRegistry : IAdminRegistry
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly ILogger<AdminRegistry> _logger;
    private readonly HashSet<long> _admins;

    public AdminRegistry(IOptions<TelegramConfig> config, ILogger<AdminRegistry> logger)
        : this(config.Value.AdminChatIds, Path.Combine(AppPaths.Root, "admins.json"), logger)
    {
    }

    /// <summary>Test seam: explicit seed + path.</summary>
    internal AdminRegistry(IEnumerable<long> seed, string path, ILogger<AdminRegistry> logger)
    {
        _logger = logger;
        _path = path;

        var stored = Load();
        if (stored is { Count: > 0 })
        {
            _admins = stored;
        }
        else
        {
            // First run (or empty file): seed from the installer-configured admins.
            _admins = new HashSet<long>(seed);
            Save();
        }
    }

    public bool IsAdmin(long chatId)
    {
        lock (_sync) { return _admins.Contains(chatId); }
    }

    public IReadOnlyList<long> All
    {
        get { lock (_sync) { return _admins.ToArray(); } }
    }

    public int Count
    {
        get { lock (_sync) { return _admins.Count; } }
    }

    public bool Add(long chatId)
    {
        lock (_sync)
        {
            if (!_admins.Add(chatId))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    public bool Remove(long chatId)
    {
        lock (_sync)
        {
            if (_admins.Count <= 1 || !_admins.Contains(chatId))
            {
                return false; // never remove the last admin
            }

            _admins.Remove(chatId);
            Save();
            return true;
        }
    }

    private HashSet<long>? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var ids = JsonSerializer.Deserialize<long[]>(File.ReadAllText(_path));
            return ids is null ? null : new HashSet<long>(ids);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load admins.json; will reseed from configuration.");
            return null;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_admins.ToArray()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist admins.json.");
        }
    }
}
