using KidControl.Contracts;
using KidControl.Infrastructure;
using KidControl.Infrastructure.Configuration;
using KidControl.Infrastructure.Ipc;
using KidControl.Infrastructure.Telegram;
using KidControl.Infrastructure.Update;
using KidControl.Platform;
using KidControl.ServiceHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0")]

var builder = Host.CreateApplicationBuilder(args);

// Version-free Windows service identity (see KidControlNames for the rationale).
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = KidControlNames.ServiceName;
});

// Override the shipped appsettings with the copy stored under protected %ProgramData%,
// which the child cannot delete. Optional so the service still boots without it.
builder.Configuration.AddJsonFile(AppPaths.OverrideConfigFile, optional: true, reloadOnChange: false);

builder.Services.AddSerilog((services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();
});

builder.Services.AddKidControlInfrastructure(builder.Configuration);

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TelegramBotBackgroundService>();
builder.Services.AddHostedService<CommandPipeServer>();
builder.Services.AddHostedService<UpdateBackgroundService>();

var host = builder.Build();

var protection = builder.Configuration
    .GetSection(ProtectionConfig.SectionName)
    .Get<ProtectionConfig>() ?? new ProtectionConfig();

var protectionLogger = host.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("ProcessProtection");

if (protection.ApplyProcessDacl)
{
    ProcessProtection.DenyTerminationForNonSystem(message => protectionLogger.LogInformation("{Message}", message));
}

if (protection.CriticalProcess)
{
    // RunCriticalAsync guarantees the critical flag is cleared in its finally block,
    // even on an unhandled crash — so a fault cannot leave the machine un-shutdownable.
    await ProcessProtection.RunCriticalAsync(
        () => host.RunAsync(),
        message => protectionLogger.LogInformation("{Message}", message));
}
else
{
    await host.RunAsync();
}
