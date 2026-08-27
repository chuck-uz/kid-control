using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Runs one-time enrollment on service start (managed mode only). Kept deliberately thin —
/// the logic lives in <see cref="FleetEnrollmentService"/>; this just invokes it once and
/// never throws, so a backend outage at boot can't stop the agent from coming up on its
/// cached policy. Heartbeat/command loops (T6/T7) will build on the identity this secures.
/// </summary>
public sealed class FleetEnrollmentHostedService(
    IServiceProvider services,
    ILogger<FleetEnrollmentHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var enrollment = scope.ServiceProvider.GetRequiredService<FleetEnrollmentService>();
            await enrollment.EnsureEnrolledAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Fleet enrollment step failed unexpectedly; continuing offline.");
        }
    }
}
