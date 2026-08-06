namespace KidControl.Application.Abstractions;

/// <summary>
/// Abstraction over wall-clock time. Introducing this is the single biggest
/// testability win over the original design, where <c>DateTimeOffset.UtcNow</c> /
/// <c>DateTimeOffset.Now</c> were called directly in dozens of places, making the
/// orchestrator's night-mode and OTP-expiry logic impossible to unit-test.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset LocalNow { get; }
}
