using KidControl.Application.Models;

namespace KidControl.Application.Abstractions;

/// <summary>Port: outbound Telegram messaging to administrators.</summary>
public interface ITelegramGateway
{
    Task SendReplyAsync(long chatId, string message, CancellationToken ct = default);

    /// <summary>Sends to every configured administrator.</summary>
    Task BroadcastAsync(string message, CancellationToken ct = default);

    Task NotifyUpdateAvailableAsync(UpdateInfo info, CancellationToken ct = default);

    Task SendPhotoAsync(long chatId, Stream photo, string? caption, CancellationToken ct = default);
}
