using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KidControl.Backend.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the model without booting the
/// web app or reaching a live database. The connection string is a placeholder — generating a
/// migration only needs the provider + naming convention, never an actual connection.
/// </summary>
public sealed class FleetDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    public FleetDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseNpgsql("Host=localhost;Database=kidcontrol;Username=kidcontrol;Password=placeholder")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new FleetDbContext(options);
    }
}
