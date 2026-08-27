using System.Reflection;
using KidControl.Backend.Auth;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Connection string comes from configuration/env (Infisical renders .env at deploy).
// Key: ConnectionStrings:Fleet  (env: ConnectionStrings__Fleet or FLEET_DB).
var connectionString =
    builder.Configuration.GetConnectionString("Fleet")
    ?? Environment.GetEnvironmentVariable("FLEET_DB")
    ?? "Host=localhost;Port=5432;Database=kidcontrol;Username=kidcontrol;Password=postgres";

builder.Services.AddDbContext<FleetDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<EnrollmentService>();

// Per-device bearer auth for agent endpoints; enrollment stays anonymous.
builder.Services.AddAuthentication(DeviceTokenAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthHandler>(DeviceTokenAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

// Apply migrations + seed the operator admin on boot. Convenience for the container deploy
// (the CLI `dotnet ef database update` path is unaffected). A DB outage here must NOT crash
// the process — liveness has to stay up so the readiness probe can report the DB as down.
if (builder.Configuration.GetValue("Fleet:AutoMigrate", true))
{
    try
    {
        await FleetSeed.MigrateAndSeedAsync(app.Services);
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
    {
        app.Logger.LogError(ex, "Startup migrate/seed failed (DB unreachable?); continuing so health can report it.");
    }
}

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

// ── Agent: enrollment (anonymous — the agent has no token yet) ──────────────────────────
app.MapPost("/agent/enroll", async (EnrollRequest req, EnrollmentService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { error = "code is required" });

    var result = await svc.EnrollAsync(req, ct);
    return result.Error switch
    {
        EnrollError.None => Results.Ok(result.Response),
        EnrollError.InvalidCode => Results.NotFound(new { error = "invalid code" }),
        EnrollError.Expired => Results.BadRequest(new { error = "code expired" }),
        EnrollError.AlreadyUsed => Results.Conflict(new { error = "code already used" }),
        _ => Results.BadRequest(new { error = "enrollment failed" })
    };
});

// ── Agent: token probe (proves the Bearer scheme works; real endpoints land in T6/T7) ────
app.MapGet("/agent/whoami", (System.Security.Claims.ClaimsPrincipal user) =>
    Results.Ok(new { deviceId = user.DeviceId(), name = user.Identity?.Name }))
    .RequireAuthorization();

// ── Operator: mint an enroll code. Temporary surface until the bot (T11) owns this. ──────
// Guarded by a static admin key (Fleet:AdminApiKey / FLEET_ADMIN_API_KEY); disabled (404) if unset.
app.MapPost("/admin/enroll-code", async (HttpRequest http, IConfiguration cfg, EnrollmentService svc,
    CancellationToken ct) =>
{
    var adminKey = cfg["Fleet:AdminApiKey"] ?? Environment.GetEnvironmentVariable("FLEET_ADMIN_API_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
        return Results.NotFound(); // feature off until configured

    if (!http.Headers.TryGetValue("X-Admin-Key", out var provided) ||
        !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided.ToString()),
            System.Text.Encoding.UTF8.GetBytes(adminKey)))
        return Results.Unauthorized();

    var code = await svc.CreateCodeAsync(actor: "operator", ct: ct);
    return Results.Ok(new { code = code.Code, expiresAt = code.ExpiresAt });
});

app.Run();

// Exposed so WebApplicationFactory-based integration tests can reference the entry point.
public partial class Program;
