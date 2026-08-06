namespace KidControl.Infrastructure.Configuration;

public sealed class TelegramConfig
{
    public const string SectionName = "Telegram";

    public string BotToken { get; init; } = string.Empty;

    /// <summary>Whitelist of administrator chat ids. Only these may issue commands.</summary>
    public long[] AdminChatIds { get; init; } = [];

    public TimeSpan NightStart { get; init; } = TimeSpan.FromHours(22);
    public TimeSpan NightEnd { get; init; } = TimeSpan.FromHours(7);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && AdminChatIds.Length > 0;

    public bool IsAdmin(long chatId) => Array.IndexOf(AdminChatIds, chatId) >= 0;
}
