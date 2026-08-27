using System.Reflection;
using KidControl.Backend.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Connection string comes from configuration/env (Infisical renders .env at deploy).
// Key: ConnectionStrings:Fleet  (env: ConnectionStrings__Fleet or FLEET_DB).
var connectionString =
    builder.Configuration.GetConnectionString("Fleet")
    ?? Environment.GetEnvironmentVariable("FLEET_DB")
    ?? "Host=localhost;Port=5432;Database=kidcontrol;Username=kidcontrol;Password=postgres";

builder.Services.AddDbContext<FleetDbContext>(options => options.UseNpgsql(connectionString));

var app = builder.Build();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

// Liveness: the app is up (never touches the DB).
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "KidControl.Backend",
    version,
    utc = DateTimeOffset.UtcNow
}));

// Readiness: can we reach PostgreSQL? 200 up / 503 down.
app.MapGet("/health/db", async (FleetDbContext db, CancellationToken ct) =>
{
    try
    {
        var ok = await db.Database.CanConnectAsync(ct);
        return ok ? Results.Ok(new { db = "up" }) : Results.StatusCode(503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { db = "down", error = ex.Message }, statusCode: 503);
    }
});

app.MapGet("/", () => Results.Ok(new { service = "KidControl.Backend", version }));

app.Run();
