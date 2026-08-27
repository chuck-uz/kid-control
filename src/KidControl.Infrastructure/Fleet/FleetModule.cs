using System.Runtime.Versioning;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Managed-mode composition (RFC §8). Registered by the host ONLY when a backend URL is
/// configured; in standalone mode none of this is wired and the agent behaves exactly as
/// before. Enrollment is the only fleet behaviour in T5 — heartbeat/commands land in T6/T7.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FleetModule
{
    /// <summary>Bind <see cref="FleetConfig"/> up front so the host can branch on managed vs standalone.</summary>
    public static FleetConfig ReadFleetConfig(this IConfiguration config)
        => config.GetSection(FleetConfig.SectionName).Get<FleetConfig>() ?? new FleetConfig();

    public static IServiceCollection AddKidControlFleet(this IServiceCollection services, FleetConfig fleet)
    {
        services.AddSingleton(fleet);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(FleetEnrollmentService.DescribeThisAgent());
        services.AddSingleton<IDeviceIdentityStore, DpapiDeviceIdentityStore>();
        services.AddScoped<FleetEnrollmentService>();

        var baseUrl = fleet.Url.EndsWith('/') ? fleet.Url : fleet.Url + "/";
        services.AddHttpClient<FleetClient>(client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KidControl-Agent/1.0");
            client.Timeout = TimeSpan.FromSeconds(90); // headroom for T7 long-poll
        });

        return services;
    }
}
