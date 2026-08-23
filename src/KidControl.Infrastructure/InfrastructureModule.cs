using System.Net.Http.Headers;
using System.Runtime.Versioning;
using KidControl.Application.Abstractions;
using KidControl.Application.Models;
using KidControl.Application.Services;
using KidControl.Domain.ValueObjects;
using KidControl.Infrastructure.Configuration;
using KidControl.Infrastructure.Ipc;
using KidControl.Infrastructure.Persistence;
using KidControl.Infrastructure.Telegram;
using KidControl.Infrastructure.Time;
using KidControl.Infrastructure.Update;
using KidControl.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace KidControl.Infrastructure;

/// <summary>
/// Composition root for the Infrastructure layer. Registers the port implementations,
/// the shared singletons, and the typed GitHub client. Hosted services
/// (<see cref="CommandPipeServer"/>, <see cref="TelegramBotBackgroundService"/>,
/// <see cref="UpdateBackgroundService"/>) are intentionally NOT registered here — the
/// process host adds the ones it needs via <c>AddHostedService&lt;T&gt;()</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InfrastructureModule
{
    public static IServiceCollection AddKidControlInfrastructure(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.Configure<TelegramConfig>(config.GetSection(TelegramConfig.SectionName));
        services.Configure<UpdateConfig>(config.GetSection(UpdateConfig.SectionName));
        services.Configure<ProtectionConfig>(config.GetSection(ProtectionConfig.SectionName));

        // Ports.
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISessionStore, JsonSessionStore>();
        services.AddSingleton<IUiNotifier, StatePipeNotifier>();
        services.AddSingleton<ISystemController, SystemController>();
        services.AddSingleton<ITelegramGateway, TelegramGateway>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // Telegram client (token-driven; a placeholder keeps DI valid when unconfigured).
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var telegram = sp.GetRequiredService<IOptions<TelegramConfig>>().Value;
            var token = string.IsNullOrWhiteSpace(telegram.BotToken) ? "0:DISABLED" : telegram.BotToken;
            return new TelegramBotClient(token);
        });

        // Fresh-session defaults seeded from configuration (night window from appsettings).
        services.AddSingleton(sp =>
        {
            var telegram = sp.GetRequiredService<IOptions<TelegramConfig>>().Value;
            var night = telegram.NightStart == telegram.NightEnd
                ? NightWindow.Default
                : new NightWindow(telegram.NightStart, telegram.NightEnd);
            return new SessionDefaults(ScheduleRule.Default, night);
        });

        // Application services that live in the composition root.
        services.AddSingleton<EmergencyOtpService>();
        services.AddSingleton<SessionService>();

        // Windows self-protection helpers.
        services.AddSingleton<ProcessWatchdog>();
        services.AddSingleton<TamperDetector>();
        services.AddSingleton<TaskSchedulerManager>();

        // Update subsystem.
        services.AddSingleton<AuthenticodeVerifier>();
        services.AddHttpClient<GitHubReleaseClient>((sp, client) =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KidControl-Updater/1.0");
            client.Timeout = TimeSpan.FromSeconds(60);

            // Only needed for a PRIVATE repo; harmless (and unset) for a public one.
            var upd = sp.GetRequiredService<IOptions<UpdateConfig>>().Value;
            if (!string.IsNullOrWhiteSpace(upd.GitHubToken))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", upd.GitHubToken);
            }
        });

        return services;
    }
}
