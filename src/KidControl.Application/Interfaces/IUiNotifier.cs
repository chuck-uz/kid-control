using KidControl.Contracts;

namespace KidControl.Application.Interfaces;

public interface IUiNotifier
{
    Task NotifyStateChangedAsync(SessionStateDto state);
}
