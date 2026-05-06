using System.Runtime.Versioning;
using KidControl.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

[SupportedOSPlatform("windows10.0")]
public sealed class TamperDetector : IDisposable
{
    private const int AlertCooldownSeconds = 60;
    private const string AlertMessage = "⚠️ Внимание! Зафиксирована попытка взлома или удаления системы контроля!";

    private readonly ITelegramNotifier _telegramNotifier;
    private readonly ILogger<TamperDetector> _logger;
    private readonly object _alertLock = new();
    private FileSystemWatcher? _watcher;
    private DateTimeOffset _lastAlertAt = DateTimeOffset.MinValue;

    public TamperDetector(ITelegramNotifier telegramNotifier, ILogger<TamperDetector> logger)
    {
        _telegramNotifier = telegramNotifier;
        _logger = logger;
    }

    public void Start()
    {
        var installDir = AppContext.BaseDirectory;
        if (!Directory.Exists(installDir))
        {
            _logger.LogWarning("TamperDetector: install directory not found: {Dir}", installDir);
            return;
        }

        _watcher = new FileSystemWatcher(installDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            Filter = "*.exe",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        _logger.LogInformation("TamperDetector: watching {Dir} for .exe deletion.", installDir);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogWarning("TamperDetector: executable deleted — {File}", e.Name);
        TrySendAlert();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Renaming away from .exe is effectively the same as deletion.
        if (!e.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ?? false)
        {
            _logger.LogWarning("TamperDetector: executable renamed away — {OldName} → {NewName}", e.OldName, e.Name);
            TrySendAlert();
        }
    }

    private void TrySendAlert()
    {
        lock (_alertLock)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastAlertAt).TotalSeconds < AlertCooldownSeconds)
            {
                return;
            }

            _lastAlertAt = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _telegramNotifier.BroadcastAsync(AlertMessage).ConfigureAwait(false);
                _logger.LogWarning("TamperDetector: tamper alert sent to Telegram.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TamperDetector: failed to send Telegram alert.");
            }
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
