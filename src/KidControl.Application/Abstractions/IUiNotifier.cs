using KidControl.Contracts;

namespace KidControl.Application.Abstractions;

/// <summary>Port: pushes session state to the user-facing UI process.</summary>
public interface IUiNotifier
{
    Task NotifyStateChangedAsync(SessionStateDto state, CancellationToken ct = default);
}
