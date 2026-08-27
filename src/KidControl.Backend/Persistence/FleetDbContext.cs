using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Persistence;

/// <summary>
/// EF Core context for the fleet backend. Entities and the initial migration are added in
/// T3; for the T2 skeleton this is intentionally empty so the app can boot, apply an
/// (empty) model, and expose a DB connectivity health check.
/// </summary>
public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
}
