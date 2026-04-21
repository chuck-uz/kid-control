using KidControl.Application.Services;
using KidControl.Infrastructure;
using KidControl.Infrastructure.Configuration;
using KidControl.Infrastructure.Ipc;
using KidControl.Infrastructure.Telegram;
using KidControl.ServiceHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0")]

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "KidControlv0.4";
});

builder.Services.AddSerilog((services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

builder.Services.AddSingleton(sp =>
{
    var uiNotifier = sp.GetRequiredService<KidControl.Application.Interfaces.IUiNotifier>();
    var telegramNotifier = sp.GetRequiredService<KidControl.Application.Interfaces.ITelegramNotifier>();
    var sessionStateRepository = sp.GetRequiredService<KidControl.Application.Interfaces.ISessionStateRepository>();
    var logger = sp.GetRequiredService<ILogger<SessionOrchestrator>>();
    var hostLifetime = sp.GetService<IHostApplicationLifetime>();

    var orchestrator = new SessionOrchestrator(
        uiNotifier,
        telegramNotifier,
        sessionStateRepository,
        logger,
        hostLifetime);
    DebugFlightRecorder.Log("Orchestrator created.");

    return orchestrator;
});
builder.Services.AddKidControlInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IndependentTimer>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TelegramBotBackgroundService>();
builder.Services.AddHostedService<NamedPipeCommandServer>();

var host = builder.Build();
await host.RunAsync();
