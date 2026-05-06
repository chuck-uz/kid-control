using KidControl.Application.Interfaces;
using KidControl.Infrastructure.Configuration;
using KidControl.Infrastructure.Ipc;
using KidControl.Infrastructure.Persistence;
using KidControl.Infrastructure.Telegram;
using KidControl.Infrastructure.Update;
using KidControl.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using Telegram.Bot;

namespace KidControl.Infrastructure;

[SupportedOSPlatform("windows10.0")]
public static class InfrastructureModule
{
    public static IServiceCollection AddKidControlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TelegramConfig>(configuration.GetSection(TelegramConfig.SectionName));
        services.Configure<UpdateConfig>(configuration.GetSection(UpdateConfig.SectionName));

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<TelegramConfig>>().Value;
            var token = string.IsNullOrWhiteSpace(config.BotToken) ? "DISABLED_BOT_TOKEN" : config.BotToken;
            return new TelegramBotClient(token);
        });

        services.AddSingleton<ITelegramNotifier, TelegramNotifier>();
        services.AddSingleton<IUiNotifier, NamedPipeUiNotifier>();
        services.AddSingleton<UiScreenshotRequester>();
        services.AddSingleton<ISessionStateRepository, JsonFileStateRepository>();
        services.AddSingleton<ProcessWatchdog>();
        services.AddSingleton<TaskSchedulerManager>();
        services.AddSingleton<TamperDetector>();

        // Update subsystem
        services.AddSingleton<HttpClient>(sp =>
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KidControl-Updater/1.0");
            client.Timeout = TimeSpan.FromSeconds(60);
            return client;
        });
        services.AddSingleton<GitHubReleaseClient>();
        services.AddSingleton<UpdateMarkerService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        return services;
    }
}
