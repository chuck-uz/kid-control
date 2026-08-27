using System.Net;
using System.Net.Http.Headers;
using System.Text;
using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>Outcome of an enroll call; carries the HTTP status so callers can log precisely.</summary>
public sealed record EnrollOutcome(bool Ok, EnrollResponse? Response, HttpStatusCode? Status, string? Error)
{
    public static EnrollOutcome Success(EnrollResponse r) => new(true, r, HttpStatusCode.OK, null);
    public static EnrollOutcome Failure(HttpStatusCode? s, string e) => new(false, null, s, e);
}

/// <summary>
/// Typed HTTP client for the fleet backend. T5 covers enrollment only; heartbeat and the
/// long-poll command endpoints are added on this same client in T6/T7. All traffic uses the
/// shared <see cref="FleetJson"/> settings so wire types can't drift from the backend.
/// </summary>
public sealed class FleetClient(HttpClient http, ILogger<FleetClient> logger)
{
    public async Task<EnrollOutcome> EnrollAsync(EnrollRequest request, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(
                FleetJson.Serialize(request), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("agent/enroll", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Enroll failed: {Status} {Body}", (int)resp.StatusCode, body);
                return EnrollOutcome.Failure(resp.StatusCode, body);
            }

            var parsed = FleetJson.Deserialize<EnrollResponse>(body);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Token))
                return EnrollOutcome.Failure(resp.StatusCode, "empty enroll response");

            return EnrollOutcome.Success(parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Backend unreachable is not fatal — the agent just stays unenrolled and retries.
            logger.LogWarning(ex, "Enroll request could not reach the backend.");
            return EnrollOutcome.Failure(null, ex.Message);
        }
    }

    /// <summary>
    /// Send a heartbeat (status up; policy/desired delta down). Returns null when the backend
    /// is unreachable or replies with an error — the caller keeps enforcing the cached policy.
    /// </summary>
    public async Task<HeartbeatResponse?> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(
                FleetJson.Serialize(request), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("agent/heartbeat", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Heartbeat failed: {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return FleetJson.Deserialize<HeartbeatResponse>(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Heartbeat could not reach the backend; staying on cached policy.");
            return null;
        }
    }

    /// <summary>Attach the device bearer token to this client for authenticated calls.</summary>
    public void UseToken(string token)
        => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
