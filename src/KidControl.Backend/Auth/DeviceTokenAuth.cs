using System.Security.Claims;
using System.Text.Encodings.Web;
using KidControl.Backend.Fleet;
using KidControl.Backend.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KidControl.Backend.Auth;

/// <summary>
/// Per-device bearer-token authentication (§5.1). The agent sends
/// <c>Authorization: Bearer &lt;token&gt;</c>; we hash it and match a non-revoked device by
/// <c>token_hash</c>. On success the device id/tenant land in claims so agent endpoints can
/// scope to the calling device. Enrollment itself is anonymous (it has no token yet).
/// </summary>
public sealed class DeviceTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    FleetDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceToken";
    public const string DeviceIdClaim = "device_id";
    public const string TenantIdClaim = "tenant_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var raw))
            return AuthenticateResult.NoResult(); // anonymous; endpoints decide if that's ok

        var header = raw.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Malformed Authorization header");

        var token = header[prefix.Length..].Trim();
        if (token.Length == 0)
            return AuthenticateResult.Fail("Empty bearer token");

        var hash = FleetTokens.HashToken(token);
        var device = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TokenHash == hash && !d.Revoked);
        if (device is null)
            return AuthenticateResult.Fail("Unknown or revoked device token");

        var identity = new ClaimsIdentity(
            [
                new Claim(DeviceIdClaim, device.Id.ToString()),
                new Claim(TenantIdClaim, device.TenantId.ToString()),
                new Claim(ClaimTypes.Name, device.Name)
            ],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}

/// <summary>Reads the authenticated device id off the current principal.</summary>
public static class DevicePrincipal
{
    public static Guid? DeviceId(this ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(DeviceTokenAuthHandler.DeviceIdClaim), out var id) ? id : null;
}
