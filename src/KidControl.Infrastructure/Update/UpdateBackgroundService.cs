using KidControl.Application.Interfaces;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Polls GitHub for new releases on a configurable interval and broadcasts a
/// Telegram inline-keyboard notification when one is available. Each tag is
/// announced at most once per host process to avoid spamming admins.
/// </summary>
public sealed class UpdateBackgroundService : BackgroundService
{
    private readonly IUpdateService _updateService;
    private readonly ITelegramNotifier _telegram;
    private readonly UpdateConfig _config;
    private readonly ILogger<UpdateBackgroundService> _logger;
    private string? _lastAnnouncedTag;

    public UpdateBackgroundService(
        IUpdateService updateService,
        ITelegramNotifier telegram,
        IOptions<UpdateConfig> config,
        ILogger<UpdateBackgroundService> logger)
    {
        _updateService = updateService;
        _telegram = telegram;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit at startup so PostUpdateNotifier (also IHostedService) gets to run first.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_config.AutoCheckEnabled)
            {
                try
                {
                    var info = await _updateService.CheckAsync(stoppingToken).ConfigureAwait(false);
                    if (info is not null && !string.Equals(info.Tag, _lastAnnouncedTag, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("Update available: {Tag} (current {Current}).", info.Tag, _updateService.GetCurrentVersion());
                        await _telegram.NotifyUpdateAvailableAsync(info, stoppingToken).ConfigureAwait(false);
                        _lastAnnouncedTag = info.Tag;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Periodic update check failed.");
                }
            }

            var hours = Math.Max(1, _config.CheckIntervalHours);
            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
