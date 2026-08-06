using KidControl.Application.Abstractions;

namespace KidControl.Infrastructure.Time;

/// <summary>Wall-clock <see cref="IClock"/> backed by the system clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}
