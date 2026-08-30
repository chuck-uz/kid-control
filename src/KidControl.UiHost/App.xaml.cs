using System.IO;
using System.Windows;
using KidControl.Contracts;
using KidControl.UiHost.Services;
using KidControl.UiHost.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Formatting.Compact;

namespace KidControl.UiHost;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (OperatingSystem.IsWindows())
        {
            // Best-effort self-protection: deny a non-SYSTEM user from killing the overlay.
            KidControl.Platform.ProcessProtection.DenyTerminationForNonSystem();
        }

        ConfigureLogging();

        var services = new ServiceCollection();
        services.AddSingleton<StatePipeClient>();
        services.AddSingleton<UiCommandServer>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<MonitorSensor>();
        }
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        _serviceProvider.GetRequiredService<StatePipeClient>().Start();
        if (OperatingSystem.IsWindows())
        {
            // RFC-05: the content-monitor sensor streams observations to the service; the service
            // toggles it on/off via the MONITOR command based on policy.
            var sensor = _serviceProvider.GetRequiredService<MonitorSensor>();
            sensor.Start();

            var uiCommands = _serviceProvider.GetRequiredService<UiCommandServer>();
            uiCommands.OnSetMonitor = sensor.SetEnabled;
            uiCommands.Start();
        }

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureLogging()
    {
        var logDir = ResolveWritableLogDirectory();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDir, "ui-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logDir, "ui-ai-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();
    }

    // The UI project intentionally does not reference Infrastructure (where AppPaths lives),
    // so the well-known logs location is reconstructed here from the shared folder name and
    // degraded gracefully if %ProgramData% is not writable in the interactive session.
    private static string ResolveWritableLogDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), KidControlNames.AppDataFolderName, "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), KidControlNames.AppDataFolderName, "logs"),
            Path.Combine(Path.GetTempPath(), KidControlNames.AppDataFolderName, "logs"),
        };

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (IOException)
            {
                // try next candidate
            }
            catch (UnauthorizedAccessException)
            {
                // try next candidate
            }
        }

        return Path.GetTempPath(); // last resort, always writable
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.GetService<StatePipeClient>()?.Stop();
        _serviceProvider?.GetService<UiCommandServer>()?.Stop();
        if (OperatingSystem.IsWindows())
        {
            _serviceProvider?.GetService<MonitorSensor>()?.Dispose();
        }
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
