namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// The agent's fleet identity, persisted after a successful enroll: the device id and its
/// bearer token. Stored encrypted under protected %ProgramData% (see
/// <see cref="IDeviceIdentityStore"/>). The token is a secret — never logged.
/// </summary>
public sealed record DeviceIdentity(
    string DeviceId,
    string Token,
    DateTimeOffset EnrolledAt,
    string BackendUrl);

/// <summary>
/// Durable, secret store for the device identity. Implementations keep the token encrypted
/// at rest; the file lives under the DACL-protected app-data root the child can't touch.
/// </summary>
public interface IDeviceIdentityStore
{
    bool IsEnrolled { get; }

    DeviceIdentity? Load();

    void Save(DeviceIdentity identity);

    /// <summary>Remove the stored identity (used on revoke / rollback to standalone).</summary>
    void Clear();
}
