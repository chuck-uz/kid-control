namespace KidControl.Backend.Entities;

/// <summary>
/// A Telegram operator allowed to manage the fleet — the central <c>AdminRegistry</c>
/// (decision #6: operator auth = Telegram whitelist). Uniqueness is per
/// (tenant, telegram_chat_id).
/// </summary>
public sealed class Admin
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; } = Tenant.DefaultId;
    public Tenant? Tenant { get; set; }

    /// <summary>Telegram chat id of the operator (the whitelist key).</summary>
    public long TelegramChatId { get; set; }

    /// <summary>Optional label for the bot UI (e.g. "мама", "папа").</summary>
    public string? Label { get; set; }
}
