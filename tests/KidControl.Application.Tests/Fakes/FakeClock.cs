using KidControl.Application.Abstractions;

namespace KidControl.Application.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="IClock"/> for tests. Both <see cref="UtcNow"/> and
/// <see cref="LocalNow"/> are freely settable and can be advanced together, so no
/// test ever touches real wall-clock time.
/// </summary>
public sealed class FakeClock : IClock
{
    // Default to a plain daytime moment (noon) so tests are not accidentally
    // inside the default 22:00–07:00 night window.
    public FakeClock()
        : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public FakeClock(DateTimeOffset now)
    {
        UtcNow = now;
        LocalNow = now;
    }

    public DateTimeOffset UtcNow { get; set; }

    public DateTimeOffset LocalNow { get; set; }

    /// <summary>Advances both clocks by the same amount.</summary>
    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        LocalNow += by;
    }

    /// <summary>Sets only the local clock (e.g. to drive night-window logic).</summary>
    public void SetLocal(DateTimeOffset local) => LocalNow = local;

    /// <summary>Sets both clocks to the same absolute moment.</summary>
    public void SetAll(DateTimeOffset now)
    {
        UtcNow = now;
        LocalNow = now;
    }
}
