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
using System.Runtime.InteropServices;
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

// Override with appsettings.json stored in ProgramData (protected from deletion by children).
var programDataConfig = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "KidControl", "appsettings.json");
builder.Configuration.AddJsonFile(programDataConfig, optional: true, reloadOnChange: false);

var host = builder.Build();

var protectionConfig = builder.Configuration
    .GetSection(ProtectionConfig.SectionName)
    .Get<ProtectionConfig>() ?? new ProtectionConfig();

ApplyProcessDacl();

if (protectionConfig.CriticalProcess)
{
    TryMarkProcessAsCritical();
}

await host.RunAsync();

// Unmark as critical before normal exit so graceful shutdown does not BSOD.
if (protectionConfig.CriticalProcess)
{
    TryUnmarkProcessAsCritical();
}

[SupportedOSPlatform("windows")]
static void TryMarkProcessAsCritical()
{
    try
    {
        RtlSetProcessIsCritical(true, out _, false);
        DebugFlightRecorder.Log("Process marked as critical (BSOD on kill enabled).");
    }
    catch (Exception ex)
    {
        DebugFlightRecorder.Log($"Failed to mark process as critical: {ex.Message}");
    }
}

[SupportedOSPlatform("windows")]
static void TryUnmarkProcessAsCritical()
{
    try
    {
        RtlSetProcessIsCritical(false, out _, false);
    }
    catch { /* best effort */ }
}

[DllImport("ntdll.dll", SetLastError = false)]
[SupportedOSPlatform("windows")]
static extern int RtlSetProcessIsCritical(
    [MarshalAs(UnmanagedType.Bool)] bool bNew,
    [MarshalAs(UnmanagedType.Bool)] out bool bOld,
    [MarshalAs(UnmanagedType.Bool)] bool bNeedScb);

// ─── DACL self-protection ─────────────────────────────────────────────────

[SupportedOSPlatform("windows")]
static void ApplyProcessDacl()
{
    // DACL rules:
    //   (D;;0x1;;;BU) - DENY  PROCESS_TERMINATE for BUILTIN\Users
    //   (D;;0x1;;;BA) - DENY  PROCESS_TERMINATE for BUILTIN\Administrators
    //   (A;;0x1FFFFF;;;SY) - ALLOW full access for SYSTEM
    //   (A;;GXRC;;;BA) - ALLOW generic execute + read-control for Admins
    //   (A;;GXRC;;;BU) - ALLOW generic execute + read-control for Users
    const string sddl = "D:(D;;0x1;;;BU)(D;;0x1;;;BA)(A;;0x1FFFFF;;;SY)(A;;GXRC;;;BA)(A;;GXRC;;;BU)";
    const uint daclSecInfo = 0x00000004;

    try
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var pSd, out _))
        {
            DebugFlightRecorder.Log($"ApplyProcessDacl: ConvertStringSD failed. Win32={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            // GetCurrentProcess() returns a pseudo-handle that SetKernelObjectSecurity
            // silently rejects when modifying the DACL. A real handle opened with
            // WRITE_DAC is required for the security change to take effect.
            const uint writeDac = 0x00040000;
            var hProcess = OpenProcess(writeDac, false, (uint)Environment.ProcessId);
            if (hProcess == IntPtr.Zero)
            {
                DebugFlightRecorder.Log($"ApplyProcessDacl: OpenProcess failed. Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                if (!SetKernelObjectSecurity(hProcess, daclSecInfo, pSd))
                {
                    DebugFlightRecorder.Log($"ApplyProcessDacl: SetKernelObjectSecurity failed. Win32={Marshal.GetLastWin32Error()}");
                    return;
                }

                DebugFlightRecorder.Log("ApplyProcessDacl: PROCESS_TERMINATE denied for Users/Admins.");
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        finally
        {
            LocalFree(pSd);
        }
    }
    catch (Exception ex)
    {
        DebugFlightRecorder.Log($"ApplyProcessDacl: exception — {ex.Message}");
    }
}

[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
[SupportedOSPlatform("windows")]
static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
    string stringSecurityDescriptor,
    uint stringSDRevision,
    out IntPtr securityDescriptor,
    out uint securityDescriptorSize);

[DllImport("advapi32.dll", SetLastError = true)]
[SupportedOSPlatform("windows")]
static extern bool SetKernelObjectSecurity(
    IntPtr handle,
    uint securityInformation,
    IntPtr securityDescriptor);

[DllImport("kernel32.dll", SetLastError = true)]
[SupportedOSPlatform("windows")]
static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

[DllImport("kernel32.dll", SetLastError = true)]
[SupportedOSPlatform("windows")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool CloseHandle(IntPtr hObject);

[DllImport("kernel32.dll")]
[SupportedOSPlatform("windows")]
static extern IntPtr LocalFree(IntPtr hMem);
