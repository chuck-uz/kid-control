using KidControl.Domain.ValueObjects;

namespace KidControl.Application.Models;

/// <summary>
/// Initial values applied to a brand-new session (before any persisted snapshot
/// exists). Lets the composition root seed configured defaults (e.g. the night
/// window from appsettings) without the application layer depending on
/// infrastructure configuration types.
/// </summary>
public sealed record SessionDefaults(ScheduleRule Rule, NightWindow Night)
{
    public static SessionDefaults Standard { get; } =
        new(ScheduleRule.Default, NightWindow.Default);
}
