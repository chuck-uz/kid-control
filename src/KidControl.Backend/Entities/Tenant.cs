namespace KidControl.Backend.Entities;

/// <summary>
/// A single family (decision #1: one tenant, multi-tenancy reserved but not built).
/// Exactly one row is seeded; <see cref="Id"/> is a fixed well-known GUID so every other
/// row can reference it deterministically.
/// </summary>
public sealed class Tenant
{
    /// <summary>The reserved single-family tenant id (see <see cref="FleetSeed"/>).</summary>
    public static readonly Guid DefaultId = Guid.Parse("00000000-0000-0000-0000-0000000f1eef");

    public Guid Id { get; set; } = DefaultId;
    public string Name { get; set; } = "Семья";

    public ICollection<Admin> Admins { get; set; } = new List<Admin>();
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}
