namespace KidControl.Infrastructure.Configuration;

public sealed class TelegramConfig
{
    public const string SectionName = "TelegramConfig";

    public string BotToken { get; init; } = string.Empty;
    public long[] AdminChatIds { get; init; } = [];
}
