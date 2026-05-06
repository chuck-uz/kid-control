using KidControl.Application.Models;

namespace KidControl.Application.Interfaces;

public interface ITelegramNotifier
{
    Task SendReplyAsync(long chatId, string message);
    Task BroadcastAsync(string message);
    Task SendPhotoAsync(long chatId, string filePath, string? caption = null);

    /// <summary>
    /// Broadcasts an "update available" message with inline buttons [Обновить] / [Позже].
    /// Buttons map to callbacks update_install_&lt;tag&gt; / update_later_&lt;tag&gt;.
    /// </summary>
    Task NotifyUpdateAvailableAsync(UpdateInfoDto info, CancellationToken ct = default);
}
