using KidControl.Fleet.Contracts;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// The agent's durable copy of what the backend last told it: the policy it has applied and
/// the desired-state it has seen, with their versions. Persisted locally so the agent can
/// enforce the last-known policy on boot without the backend (offline-first, RFC §7). Not
/// secret — this is policy, not credentials (the token lives in <see cref="IDeviceIdentityStore"/>).
/// </summary>
public sealed class FleetState
{
    public PolicyDto? Policy { get; set; }
    public DesiredStateDto? Desired { get; set; }

    public int PolicyVersion => Policy?.Version ?? 0;
    public int DesiredVersion => Desired?.Version ?? 0;
}

/// <summary>Durable store for <see cref="FleetState"/> (plain JSON under protected %ProgramData%).</summary>
public interface IFleetStateStore
{
    FleetState Load();
    void Save(FleetState state);
}
