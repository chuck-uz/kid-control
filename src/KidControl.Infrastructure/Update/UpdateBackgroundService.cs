using KidControl.Application.Abstractions;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Periodically checks for a newer release. When <see cref="UpdateConfig.AutoInstall"/> is on
/// (the default) it downloads, verifies and installs the update automatically — the service
/// restarts on the new version, preserving config and the child's timer. Otherwise it only
/// notifies admins over Telegram. Each tag is handled at most once per host process to avoid
/// re-spamming (and re-attempting a broken release) every interval.
/// </summary>
public sealed class UpdateBackgroundService(
    IUpdateService updateService,
    ITelegramGateway telegram,
    IOptions<UpdateConfig> config,
    Fleet.FleetUpdateTarget updateTarget,
    ILogger<UpdateBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly UpdateConfig _config = config.Value;
    private string? _handledTag;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            logger.LogInformation("Update background service disabled by configuration.");
            return;
        }

        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var interval = _config.CheckInterval > TimeSpan.Zero ? _config.CheckInterval : TimeSpan.FromHours(6);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Managed mode may pin a target version (§9). A pinned target overrides the
                // "track latest" behaviour and can move up OR down to exactly that tag.
                var target = updateTarget.Current;
                if (Fleet.FleetUpdateTarget.NeedsPinnedInstall(updateService.CurrentVersionText, target))
                {
                    await HandlePinnedTargetAsync(target, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    var info = await updateService.CheckAsync(stoppingToken).ConfigureAwait(false);
                    if (info is not null && !string.Equals(info.Tag, _handledTag, StringComparison.Ordinal))
                    {
                        if (_config.AutoInstall)
                        {
                            await AutoInstallAsync(info, stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            logger.LogInformation("Update available: {Tag} (current {Current}).", info.Tag, updateService.CurrentVersion);
                            await telegram.NotifyUpdateAvailableAsync(info, stoppingToken).ConfigureAwait(false);
                            _handledTag = info.Tag;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Periodic update check failed.");
            }

            try { await Task.Delay(interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AutoInstallAsync(UpdateInfo info, CancellationToken ct)
    {
        // Mark handled up-front: whether it succeeds (service restarts) or fails (bad release),
        // we must not re-attempt the same tag every interval.
        _handledTag = info.Tag;

        logger.LogInformation("Auto-update: installing {Tag} (current {Current}).", info.Tag, updateService.CurrentVersion);
        await SafeNotifyAsync($"⬇️ Устанавливаю обновление {info.Tag}. Служба перезапустится автоматически.", ct)
            .ConfigureAwait(false);

        try
        {
            // Downloads the setup zip, verifies size + SHA-256 + Authenticode signature/thumbprint
            // for every executable, then runs the installer's binary-only /update and stops this
            // service so it restarts on the new version.
            await updateService.StartInstallAsync(info.Tag, ct).ConfigureAwait(false);
            logger.LogInformation("Auto-update: installer launched for {Tag}; service stop scheduled.", info.Tag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-update to {Tag} failed.", info.Tag);
            await SafeNotifyAsync(
                $"⚠️ Автообновление до {info.Tag} не удалось: {ex.Message}. Обновите вручную (update.bat).", ct)
                .ConfigureAwait(false);
        }
    }

    private async Task HandlePinnedTargetAsync(string target, CancellationToken ct)
    {
        var tag = target.StartsWith('v') || target.StartsWith('V') ? target : "v" + target;
        if (string.Equals(tag, _handledTag, StringComparison.Ordinal))
            return; // already attempted this pinned tag

        if (!_config.AutoInstall)
        {
            logger.LogInformation("Pinned update target {Tag} (current {Current}); auto-install off — notify only.",
                tag, updateService.CurrentVersionText);
            _handledTag = tag;
            return;
        }

        _handledTag = tag;
        logger.LogInformation("Pinned update: installing {Tag} (current {Current}).", tag, updateService.CurrentVersionText);
        await SafeNotifyAsync($"⬇️ Устанавливаю целевую версию {tag}. Служба перезапустится автоматически.", ct)
            .ConfigureAwait(false);

        try
        {
            await updateService.StartInstallAsync(tag, ct).ConfigureAwait(false);
            logger.LogInformation("Pinned update: installer launched for {Tag}.", tag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pinned update to {Tag} failed.", tag);
            await SafeNotifyAsync($"⚠️ Установка целевой версии {tag} не удалась: {ex.Message}.", ct).ConfigureAwait(false);
        }
    }

    private async Task SafeNotifyAsync(string message, CancellationToken ct)
    {
        try
        {
            await telegram.BroadcastAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Auto-update notification failed (non-fatal).");
        }
    }
}
