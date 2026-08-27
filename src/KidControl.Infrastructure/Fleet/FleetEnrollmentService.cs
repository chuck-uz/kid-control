using KidControl.Fleet.Contracts;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>Machine facts sent at enroll time. Injected so it can be faked in tests.</summary>
public sealed record AgentInfo(string MachineName, string OsInfo, string AgentVersion);

public enum EnrollmentStep { NotManaged, AlreadyEnrolled, NoCode, Enrolled, Failed }

/// <summary>
/// Drives one-time enrollment (RFC §8): in managed mode, if the device isn't enrolled yet
/// and an enroll code is configured, exchange it for a device token and persist the identity.
/// Idempotent — once enrolled it just re-attaches the stored token to the client. Backend
/// being unreachable is non-fatal: it returns <see cref="EnrollmentStep.Failed"/> and the
/// agent keeps running on its cached policy (offline-first).
/// </summary>
public sealed class FleetEnrollmentService(
    FleetConfig config,
    FleetClient client,
    IDeviceIdentityStore store,
    AgentInfo agent,
    TimeProvider clock,
    ILogger<FleetEnrollmentService> logger)
{
    public async Task<EnrollmentStep> EnsureEnrolledAsync(CancellationToken ct = default)
    {
        if (!config.IsManaged)
            return EnrollmentStep.NotManaged;

        var existing = store.Load();
        if (existing is not null)
        {
            client.UseToken(existing.Token);
            logger.LogInformation("Fleet: already enrolled as device {DeviceId}.", existing.DeviceId);
            return EnrollmentStep.AlreadyEnrolled;
        }

        if (!config.HasEnrollCode)
        {
            logger.LogWarning(
                "Fleet: managed mode is on but no enroll code is configured and the device isn't enrolled. " +
                "Set Fleet:EnrollCode once to enroll.");
            return EnrollmentStep.NoCode;
        }

        var request = new EnrollRequest(config.EnrollCode, agent.MachineName, agent.OsInfo, agent.AgentVersion);
        var outcome = await client.EnrollAsync(request, ct);
        if (!outcome.Ok || outcome.Response is null)
        {
            logger.LogWarning("Fleet: enrollment did not succeed ({Status}). Will retry on next start.",
                outcome.Status);
            return EnrollmentStep.Failed;
        }

        var identity = new DeviceIdentity(
            outcome.Response.DeviceId, outcome.Response.Token, clock.GetUtcNow(), config.Url);
        store.Save(identity);
        client.UseToken(identity.Token);
        logger.LogInformation("Fleet: enrolled as device {DeviceId}.", identity.DeviceId);
        return EnrollmentStep.Enrolled;
    }

    /// <summary>Convenience factory reading agent facts from the environment + entry assembly.</summary>
    public static AgentInfo DescribeThisAgent()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? typeof(FleetEnrollmentService).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        return new AgentInfo(Environment.MachineName, System.Runtime.InteropServices.RuntimeInformation.OSDescription, version);
    }
}
