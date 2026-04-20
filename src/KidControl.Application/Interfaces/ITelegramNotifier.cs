namespace KidControl.Application.Interfaces;

public interface ITelegramNotifier
{
    Task SendReplyAsync(long chatId, string message);
    Task BroadcastAsync(string message);
}
