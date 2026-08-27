using System.Collections.Concurrent;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Wakes a device's long-poll the moment a command is enqueued for it, so operators see
/// near-instant delivery without the agent tight-polling. Purely an optimization: if a signal
/// is missed (process restart, race), the poll still returns when its timeout elapses and the
/// agent retries. One lightweight semaphore per device, created on demand.
/// </summary>
public sealed class CommandSignal
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    private SemaphoreSlim Gate(Guid deviceId) => _gates.GetOrAdd(deviceId, _ => new SemaphoreSlim(0));

    /// <summary>Signal that a device has a new command (release any current waiter).</summary>
    public void Notify(Guid deviceId)
    {
        var gate = Gate(deviceId);
        // Cap the count so a burst of enqueues can't inflate it unbounded.
        if (gate.CurrentCount == 0)
            gate.Release();
    }

    /// <summary>Wait up to <paramref name="timeout"/> for a new-command signal for this device.</summary>
    public Task<bool> WaitAsync(Guid deviceId, TimeSpan timeout, CancellationToken ct)
        => Gate(deviceId).WaitAsync(timeout, ct);
}
