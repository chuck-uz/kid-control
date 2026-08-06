using KidControl.Application.Models;

namespace KidControl.Application.Abstractions;

/// <summary>Port: durable persistence of session state across restarts.</summary>
public interface ISessionStore
{
    /// <summary>Returns the persisted snapshot, or null if none exists / it is unreadable.</summary>
    SessionSnapshot? Load();

    /// <summary>Persists the snapshot atomically (temp-file + replace).</summary>
    void Save(SessionSnapshot snapshot);
}
