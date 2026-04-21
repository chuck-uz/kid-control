using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using KidControl.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Ipc;

[SupportedOSPlatform("windows10.0")]
public sealed class NamedPipeCommandServer(
    SessionOrchestrator orchestrator,
    ILogger<NamedPipeCommandServer> logger) : BackgroundService
{
    private const string PipeName = "KidControlCommandPipe";
    private const string ServiceName = "KidControlv0.4";
    private const string UiProcessName = "KidControl.UiHost";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Named pipe command server started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = NamedPipeServerFactory.CreateDuplex(PipeName, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                await using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };

                var command = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(command))
                {
                    await writer.WriteLineAsync("ERROR").ConfigureAwait(false);
                    continue;
                }

                var response = await HandleCommandAsync(command.Trim()).ConfigureAwait(false);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Named pipe command server iteration failed.");
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> HandleCommandAsync(string command)
    {
        if (string.Equals(command, "INITIATE_EMERGENCY_AUTH", StringComparison.OrdinalIgnoreCase))
        {
            await orchestrator.GenerateAndSendOtpAsync().ConfigureAwait(false);
            return "OTP_SENT";
        }

        const string prefix = "EMERGENCY_SHUTDOWN:";
        if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var otp = command[prefix.Length..];
            if (!orchestrator.ValidateEmergencyOtp(otp))
            {
                return "INVALID_OTP";
            }

            TryKillUiProcess();
            TryStopCurrentService();
            return "SUCCESS";
        }

        return "UNKNOWN_COMMAND";
    }

    private void TryKillUiProcess()
    {
        foreach (var process in Process.GetProcessesByName(UiProcessName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to kill UI process {ProcessId}.", process.Id);
            }
        }
    }

    private void TryStopCurrentService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.Paused)
            {
                controller.Stop();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ServiceController stop failed, trying sc.exe fallback.");
            try
            {
                using var scProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"stop {ServiceName}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                scProcess?.WaitForExit(3000);
            }
            catch (Exception fallbackEx)
            {
                logger.LogError(fallbackEx, "sc.exe stop fallback failed.");
            }
        }
    }
}
