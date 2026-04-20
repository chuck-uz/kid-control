using KidControl.Contracts;

namespace KidControl.Application;

public interface ITimeControlService
{
    Task HandleCommandAsync(ControlCommand command, CancellationToken cancellationToken);
}
