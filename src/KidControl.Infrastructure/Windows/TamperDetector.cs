using System.Runtime.Versioning;
using KidControl.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Windows;

/// <summary>
/// Watches the install directory and raises a Telegram alert when a protecting executable
/// is deleted or renamed away.
///
/// Fixes over the original watcher:
///  * subscribes to <see cref="FileSystemWatcher.Error"/> and re-arms on buffer overflow
///    (dropped events used to blind the detector silently);
///  * watches recursively so tampering in subdirectories is seen;
///  * clean <c>.exe</c> predicate instead of the original's inverted nullable-bool test.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TamperDetector(ITelegramGateway telegram, ILogger<TamperDetector> logger) : IDisposable
{
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromSeconds(60);
    private const string AlertMessage = "⚠️ Внимание! Зафиксирована попытка удаления или подмены системы контроля!";

    private readonly object _alertSync = new();
    private FileSystemWatcher? _watcher;
    private DateTimeOffset _lastAlertAt = DateTimeOffset.MinValue;

    public void Start()
    {
        var installDir = AppContext.BaseDirectory;
        if (!Directory.Exists(installDir))
        {
            logger.LogWarning("TamperDetector: install directory not found: {Dir}", installDir);
            return;
        }

        _watcher = CreateWatcher(installDir);
        logger.LogInformation("TamperDetector: watching {Dir} (recursive) for .exe removal.", installDir);
    }

    private FileSystemWatcher CreateWatcher(string dir)
    {
        var watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024
        };

        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsExecutable(e.Name))
        {
            logger.LogWarning("TamperDetector: executable deleted — {File}", e.Name);
            TrySendAlert();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Renaming a .exe to a non-.exe name is effectively removing the protection.
        if (IsExecutable(e.OldName) && !IsExecutable(e.Name))
        {
            logger.LogWarning("TamperDetector: executable renamed away — {Old} → {New}", e.OldName, e.Name);
            TrySendAlert();
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        logger.LogError(e.GetException(), "TamperDetector: watcher error; re-arming.");
        ReArm();
    }

    private void ReArm()
    {
        try
        {
            var dir = _watcher?.Path ?? AppContext.BaseDirectory;
            DisposeWatcher();
            if (Directory.Exists(dir))
            {
                _watcher = CreateWatcher(dir);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TamperDetector: failed to re-arm watcher.");
        }
    }

    private void TrySendAlert()
    {
        lock (_alertSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastAlertAt < AlertCooldown)
            {
                return;
            }

            _lastAlertAt = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await telegram.BroadcastAsync(AlertMessage).ConfigureAwait(false);
                logger.LogWarning("TamperDetector: alert sent.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TamperDetector: failed to send alert.");
            }
        });
    }

    private static bool IsExecutable(string? name)
        => name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose() => DisposeWatcher();
}
