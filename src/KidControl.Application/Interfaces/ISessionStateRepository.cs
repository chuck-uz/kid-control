using KidControl.Application.Models;

namespace KidControl.Application.Interfaces;

public interface ISessionStateRepository
{
    SessionPersistenceState? Load();
    void Save(SessionPersistenceState state);
}
