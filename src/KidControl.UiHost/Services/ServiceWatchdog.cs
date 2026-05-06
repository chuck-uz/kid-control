using System.Diagnostics;
using Serilog;

namespace KidControl.UiHost.Services;

public sealed class ServiceWatchdog : IDisposable
{
    private const string ServiceProcessName = "KidControl.ServiceHost";
    private const string RestartTaskName = "KidControl.Service.Restart";
    private const int CheckIntervalMs = 5000;

    private readonly CancellationTokenSource _cts = new();
    private Task? _watchTask;

    public void Start()
    {
        _watchTask = Task.Run(RunAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    private async Task RunAsync()
    {
        // Initial delay so the service has time to finish starting before first check.
        await Task.Delay(CheckIntervalMs, _cts.Token).ConfigureAwait(false);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var procs = Process.GetProcessesByName(ServiceProcessName);
                if (procs.Length == 0)
                {
                    Log.Warning("ServiceWatchdog: {Process} not running — triggering restart task.", ServiceProcessName);
                    TryRestartViaScheduledTask();
                }

                foreach (var p in procs)
                {
                    p.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ServiceWatchdog: check failed.");
            }

            try
            {
                await Task.Delay(CheckIntervalMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void TryRestartViaScheduledTask()
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{RestartTaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            proc.WaitForExit(5000);
            Log.Information("ServiceWatchdog: schtasks /Run exit code = {Code}", proc.ExitCode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ServiceWatchdog: failed to trigger restart task.");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _watchTask?.GetAwaiter().GetResult(); } catch { /* ignored */ }
        _cts.Dispose();
    }
}
