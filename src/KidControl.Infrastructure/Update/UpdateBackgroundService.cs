using KidControl.Application.Abstractions;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Periodically checks for a newer release and notifies admins over Telegram. Each tag is
/// announced at most once per host process to avoid re-spamming on every interval.
/// </summary>
public sealed class UpdateBackgroundService(
    IUpdateService updateService,
    ITelegramGateway telegram,
    IOptions<UpdateConfig> config,
    ILogger<UpdateBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private readonly UpdateConfig _config = config.Value;
    private string? _lastAnnouncedTag;

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
                var info = await updateService.CheckAsync(stoppingToken).ConfigureAwait(false);
                if (info is not null && !string.Equals(info.Tag, _lastAnnouncedTag, StringComparison.Ordinal))
                {
                    logger.LogInformation("Update available: {Tag} (current {Current}).", info.Tag, updateService.CurrentVersion);
                    await telegram.NotifyUpdateAvailableAsync(info, stoppingToken).ConfigureAwait(false);
                    _lastAnnouncedTag = info.Tag;
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
}
