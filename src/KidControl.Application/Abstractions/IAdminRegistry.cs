namespace KidControl.Application.Abstractions;

/// <summary>
/// The set of Telegram chat ids allowed to control the app. Seeded from configuration
/// on first run, then editable at runtime (via the bot) and persisted, so administrators
/// can be added/removed without reinstalling.
/// </summary>
public interface IAdminRegistry
{
    bool IsAdmin(long chatId);

    IReadOnlyList<long> All { get; }

    int Count { get; }

    /// <summary>Adds an admin. Returns true if it was newly added.</summary>
    bool Add(long chatId);

    /// <summary>Removes an admin. Returns false if absent or if it is the last one (never allowed).</summary>
    bool Remove(long chatId);
}
