namespace KidControl.Infrastructure.Configuration;

public sealed class TelegramConfig
{
    public const string SectionName = "TelegramConfig";

    public string BotToken { get; init; } = string.Empty;
    public long[] AdminChatIds { get; init; } = [];
    public string NightModeStart { get; init; } = "22:00";
    public string NightModeEnd { get; init; } = "07:00";
}
