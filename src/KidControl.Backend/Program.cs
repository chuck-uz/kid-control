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
builder.Services.AddSingleton<CommandSignal>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<HeartbeatService>();
builder.Services.AddScoped<DeviceAdminService>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddScoped<DbAdminRegistry>();
builder.Services.AddScoped<FleetBotActions>();

// Fleet operator bot (T11): long-polls Telegram, drives the fleet services. A placeholder
// token keeps DI valid when unconfigured; the hosted service no-ops until a real token is set.
var botToken = builder.Configuration["Telegram:BotToken"]
    ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
builder.Services.AddSingleton<Telegram.Bot.ITelegramBotClient>(
    new Telegram.Bot.TelegramBotClient(string.IsNullOrWhiteSpace(botToken) ? "0:DISABLED" : botToken));
builder.Services.AddHostedService<FleetBotBackgroundService>();

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

// ── Agent: token probe (proves the Bearer scheme works) ──────────────────────────────────
app.MapGet("/agent/whoami", (System.Security.Claims.ClaimsPrincipal user) =>
    Results.Ok(new { deviceId = user.DeviceId(), name = user.Identity?.Name }))
    .RequireAuthorization();

// ── Agent: heartbeat — report status + versions, receive policy/desired delta (§5.1) ─────
app.MapPost("/agent/heartbeat", async (HeartbeatRequest req, System.Security.Claims.ClaimsPrincipal user,
    HeartbeatService svc, CancellationToken ct) =>
{
    var deviceId = user.DeviceId();
    if (deviceId is null)
        return Results.Unauthorized();

    var resp = await svc.HandleAsync(deviceId.Value, req, ct);
    return resp is null ? Results.Unauthorized() : Results.Ok(resp);
}).RequireAuthorization();

// ── Agent: long-poll for pending commands, and ack executed ones (§5.1) ──────────────────
app.MapGet("/agent/commands", async (int? wait, System.Security.Claims.ClaimsPrincipal user,
    CommandService svc, CancellationToken ct) =>
{
    var deviceId = user.DeviceId();
    if (deviceId is null)
        return Results.Unauthorized();

    var waitFor = TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 60)); // cap the long-poll
    var commands = await svc.PollAsync(deviceId.Value, waitFor, ct);
    return Results.Ok(commands);
}).RequireAuthorization();

app.MapPost("/agent/commands/ack", async (CommandAckBatch batch, System.Security.Claims.ClaimsPrincipal user,
    CommandService svc, CancellationToken ct) =>
{
    var deviceId = user.DeviceId();
    if (deviceId is null)
        return Results.Unauthorized();

    await svc.AckAsync(deviceId.Value, batch, ct);
    return Results.Ok();
}).RequireAuthorization();

// ── Operator surface. Temporary (X-Admin-Key), until the bot (T11) owns it. ───────────────
// Mint an enroll code.
app.MapPost("/admin/enroll-code", async (HttpRequest http, IConfiguration cfg, EnrollmentService svc,
    CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    var code = await svc.CreateCodeAsync(actor: "operator", ct: ct);
    return Results.Ok(new { code = code.Code, expiresAt = code.ExpiresAt });
});

// List devices with their live status + policy version.
app.MapGet("/admin/devices", async (HttpRequest http, IConfiguration cfg, DeviceAdminService svc,
    CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    return Results.Ok(await svc.ListDevicesAsync(ct));
});

// Edit a device policy (bumps the version → propagates on the device's next heartbeat).
app.MapPost("/admin/devices/{id:guid}/policy", async (Guid id, PolicyPatch patch, HttpRequest http,
    IConfiguration cfg, DeviceAdminService svc, CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    var version = await svc.UpdatePolicyAsync(id, patch, ct: ct);
    return version is null ? Results.NotFound() : Results.Ok(new { policyVersion = version });
});

// Pause / resume a device (desired-state override).
app.MapPost("/admin/devices/{id:guid}/pause", async (Guid id, PauseRequest body, HttpRequest http,
    IConfiguration cfg, DeviceAdminService svc, CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    var version = await svc.SetPausedAsync(id, body.Paused, ct: ct);
    return version is null ? Results.NotFound() : Results.Ok(new { desiredVersion = version, paused = body.Paused });
});

// Force-block / release a device (desired-state override).
app.MapPost("/admin/devices/{id:guid}/block", async (Guid id, BlockRequest body, HttpRequest http,
    IConfiguration cfg, DeviceAdminService svc, CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    var version = await svc.SetForceBlockedAsync(id, body.Blocked, ct: ct);
    return version is null ? Results.NotFound() : Results.Ok(new { desiredVersion = version, blocked = body.Blocked });
});

// Set / clear the night-bypass window (desired-state override).
app.MapPost("/admin/devices/{id:guid}/night-bypass", async (Guid id, NightBypassRequest body, HttpRequest http,
    IConfiguration cfg, DeviceAdminService svc, CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    var version = await svc.SetNightBypassAsync(id, body.Until, ct: ct);
    return version is null ? Results.NotFound() : Results.Ok(new { desiredVersion = version, until = body.Until });
});

// Queue a one-shot command (e.g. add_time). Default TTL 5 min (§7: overrides expire fast).
app.MapPost("/admin/devices/{id:guid}/commands", async (Guid id, EnqueueCommandRequest body, HttpRequest http,
    IConfiguration cfg, CommandService svc, CancellationToken ct) =>
{
    if (AdminGuard(http, cfg) is { } deny) return deny;
    if (string.IsNullOrWhiteSpace(body.Type))
        return Results.BadRequest(new { error = "type is required" });

    var ttl = TimeSpan.FromSeconds(Math.Clamp(body.TtlSeconds ?? 300, 5, 86_400));
    var commandId = await svc.EnqueueAsync(id, body.Type, body.Payload, ttl, ct: ct);
    return commandId is null ? Results.NotFound() : Results.Ok(new { commandId, ttlSeconds = (int)ttl.TotalSeconds });
});

app.Run();

// Shared guard for the temporary operator API: returns a deny-result, or null when allowed.
// Disabled (404) unless Fleet:AdminApiKey / FLEET_ADMIN_API_KEY is set; else requires X-Admin-Key.
static IResult? AdminGuard(HttpRequest http, IConfiguration cfg)
{
    var adminKey = cfg["Fleet:AdminApiKey"] ?? Environment.GetEnvironmentVariable("FLEET_ADMIN_API_KEY");
    if (string.IsNullOrWhiteSpace(adminKey))
        return Results.NotFound();

    var provided = http.Headers["X-Admin-Key"].ToString();
    var ok = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(provided),
        System.Text.Encoding.UTF8.GetBytes(adminKey));
    return ok ? null : Results.Unauthorized();
}

// Exposed so WebApplicationFactory-based integration tests can reference the entry point.
public partial class Program;
