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

        _serviceProvider = services.BuildServiceProvider();

        _serviceProvider.GetRequiredService<NamedPipeClient>().Start();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static string ResolveWritableUiLogDirectory()
    {
        var sharedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KidControl",
            "logs");

        try
        {
            Directory.CreateDirectory(sharedDir);
            return sharedDir;
        }
        catch (UnauthorizedAccessException)
        {
            // UiHost runs in user session and may not have write permissions to ProgramData after hardened ACL setup.
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KidControl",
                "logs");
            Directory.CreateDirectory(localDir);
            return localDir;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.GetService<NamedPipeClient>()?.Stop();
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
