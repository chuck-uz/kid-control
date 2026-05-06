using KidControl.UiHost.Services;
using KidControl.UiHost.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
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
            Services.ProcessProtection.Apply();
        }

        var logDir = ResolveWritableUiLogDirectory();
        var textLog = Path.Combine(logDir, "ui-.log");
        var jsonLog = Path.Combine(logDir, "ui-ai-.json");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: textLog,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: jsonLog,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<NamedPipeClient>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<UiCommandPipeServer>();
        services.AddSingleton<ServiceWatchdog>();

        _serviceProvider = services.BuildServiceProvider();

        _serviceProvider.GetRequiredService<NamedPipeClient>().Start();
        _serviceProvider.GetRequiredService<UiCommandPipeServer>().Start();
        _serviceProvider.GetRequiredService<ServiceWatchdog>().Start();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static string ResolveWritableUiLogDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KidControl", "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "KidControl", "logs"),
            Path.Combine(Path.GetTempPath(), "KidControl", "logs"),
        };

        foreach (var dir in candidates)
        {
            try { Directory.CreateDirectory(dir); return dir; }
            catch { /* try next candidate */ }
        }

        return Path.GetTempPath(); // last resort, never throws
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.GetService<NamedPipeClient>()?.Stop();
        _serviceProvider?.GetService<UiCommandPipeServer>()?.Stop();
        _serviceProvider?.GetService<ServiceWatchdog>()?.Stop();
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
