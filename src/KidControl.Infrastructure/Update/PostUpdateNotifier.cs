using System.Reflection;
using KidControl.Application.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Reads the post-update marker on host start and emits a Telegram notification
/// describing whether the update/rollback succeeded.
/// </summary>
public sealed class PostUpdateNotifier : IHostedService
{
    private readonly UpdateMarkerService _marker;
    private readonly ITelegramNotifier _telegram;
    private readonly ILogger<PostUpdateNotifier> _logger;

    public PostUpdateNotifier(
        UpdateMarkerService marker,
        ITelegramNotifier telegram,
        ILogger<PostUpdateNotifier> logger)
    {
        _marker = marker;
        _telegram = telegram;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pending = _marker.TryRead();
        if (pending is null) return;

        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var current = asm.GetName().Version?.ToString() ?? "?";
        var trimmedTag = (pending.TargetTag ?? string.Empty).TrimStart('v', 'V').Trim();
        var success = !string.IsNullOrEmpty(trimmedTag) && current.StartsWith(trimmedTag, StringComparison.Ordinal);

        var verb = pending.Kind == "rollback" ? "Откат" : "Обновление";
        var message = success
            ? $"✅ {verb} до {pending.TargetTag} завершено успешно. Текущая версия: {current}."
            : $"⚠️ {verb} до {pending.TargetTag} завершилось с расхождением версий. Текущая: {current}.";

        try
        {
            await _telegram.BroadcastAsync(message).ConfigureAwait(false);
            _logger.LogInformation("Post-update notification sent: {Message}", message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send post-update notification.");
        }
        finally
        {
            _marker.Delete();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
