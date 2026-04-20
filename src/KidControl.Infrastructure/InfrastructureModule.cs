using KidControl.Application.Interfaces;
using KidControl.Infrastructure.Configuration;
using KidControl.Infrastructure.Ipc;
using KidControl.Infrastructure.Persistence;
using KidControl.Infrastructure.Telegram;
using KidControl.Infrastructure.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<TelegramConfig>>().Value;
            var token = string.IsNullOrWhiteSpace(config.BotToken) ? "DISABLED_BOT_TOKEN" : config.BotToken;
            return new TelegramBotClient(token);
        });

        services.AddSingleton<ITelegramNotifier, TelegramNotifier>();
        services.AddSingleton<IUiNotifier, NamedPipeUiNotifier>();
        services.AddSingleton<ISessionStateRepository, JsonFileStateRepository>();
        services.AddSingleton<ProcessWatchdog>();
        services.AddSingleton<TaskSchedulerManager>();

        return services;
    }
}
