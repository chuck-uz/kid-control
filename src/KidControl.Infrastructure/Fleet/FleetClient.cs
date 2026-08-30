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

/// <summary>Backend transport used by the agent; an interface so reconciliation can be tested against a fake backend.</summary>
public interface IFleetClient
{
    Task<EnrollOutcome> EnrollAsync(EnrollRequest request, CancellationToken ct = default);
    Task<HeartbeatResponse?> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CommandDto>> PollCommandsAsync(int waitSeconds, CancellationToken ct = default);
    Task AckCommandsAsync(CommandAckBatch batch, CancellationToken ct = default);

    /// <summary>Upload a requested screenshot (G1). Returns false on any failure — the operator retries.</summary>
    Task<bool> UploadMediaAsync(string uploadId, byte[] image, CancellationToken ct = default);

    /// <summary>Download an operator-sent audio clip to play (G2). Null on any failure.</summary>
    Task<byte[]?> DownloadMediaAsync(string mediaId, CancellationToken ct = default);

    /// <summary>Fetch the content-monitor lists (RFC-05). Null on any failure. Default no-op for fakes.</summary>
    Task<MonitorListsDto?> GetMonitorListsAsync(CancellationToken ct = default)
        => Task.FromResult<MonitorListsDto?>(null);

    /// <summary>Push a content-monitor hit + optional screenshot (RFC-05). Default no-op for fakes.</summary>
    Task<bool> PostAlertAsync(WordAlertDto alert, byte[]? screenshot, CancellationToken ct = default)
        => Task.FromResult(false);

    void UseToken(string token);
}

/// <summary>
/// Typed HTTP client for the fleet backend (enrollment, heartbeat, long-poll commands). All
/// traffic uses the shared <see cref="FleetJson"/> settings so wire types can't drift from the
/// backend.
/// </summary>
public sealed class FleetClient(HttpClient http, ILogger<FleetClient> logger) : IFleetClient
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

    /// <summary>
    /// Long-poll for pending commands, waiting up to <paramref name="waitSeconds"/> server-side.
    /// Returns an empty list on timeout or any error (the caller simply re-polls).
    /// </summary>
    public async Task<IReadOnlyList<CommandDto>> PollCommandsAsync(int waitSeconds, CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync($"agent/commands?wait={waitSeconds}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Command poll failed: {Status}", (int)resp.StatusCode);
                return [];
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return FleetJson.Deserialize<List<CommandDto>>(body) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Command poll could not reach the backend.");
            return [];
        }
    }

    /// <summary>Ack executed commands (idempotent). Best-effort — a failed ack just re-delivers.</summary>
    public async Task AckCommandsAsync(CommandAckBatch batch, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(
                FleetJson.Serialize(batch), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("agent/commands/ack", content, ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Command ack failed: {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Command ack could not reach the backend; will re-ack later.");
        }
    }

    public async Task<bool> UploadMediaAsync(string uploadId, byte[] image, CancellationToken ct = default)
    {
        try
        {
            using var content = new ByteArrayContent(image);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            using var resp = await http.PostAsync(
                $"agent/media?uploadId={Uri.EscapeDataString(uploadId)}", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Media upload failed: {Status}", (int)resp.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Media upload could not reach the backend.");
            return false;
        }
    }

    public async Task<byte[]?> DownloadMediaAsync(string mediaId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync(
                $"agent/audio?mediaId={Uri.EscapeDataString(mediaId)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Audio download failed: {Status}", (int)resp.StatusCode);
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Audio download could not reach the backend.");
            return null;
        }
    }

    public async Task<MonitorListsDto?> GetMonitorListsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync("agent/monitor-lists", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Monitor-lists fetch failed: {Status}", (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return System.Text.Json.JsonSerializer.Deserialize<MonitorListsDto>(json, FleetJson.Options);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Monitor-lists fetch could not reach the backend.");
            return null;
        }
    }

    public async Task<bool> PostAlertAsync(WordAlertDto alert, byte[]? screenshot, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var metaJson = System.Text.Json.JsonSerializer.Serialize(alert, FleetJson.Options);
            form.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"), "meta");
            if (screenshot is { Length: > 0 })
            {
                var img = new ByteArrayContent(screenshot);
                img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(img, "shot", "alert.png");
            }

            using var resp = await http.PostAsync("agent/alert", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Alert post failed: {Status}", (int)resp.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Alert post could not reach the backend.");
            return false;
        }
    }

    /// <summary>Attach the device bearer token to this client for authenticated calls.</summary>
    public void UseToken(string token)
        => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
