using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

[assembly: SupportedOSPlatform("windows10.0")]

const string ServiceName = "KidControlv0.4";

if (!IsRunningAsAdministrator())
{
    Console.WriteLine("Внимание: для аварийного отключения нужны права администратора. Запустите Unlocker.exe от имени администратора.");
    return;
}

Console.WriteLine("Запрос кода подтверждения отправлен в Telegram...");
var initResponse = await SendCommandAsync("INITIATE_EMERGENCY_AUTH");
if (!string.Equals(initResponse, "OTP_SENT", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Не удалось инициализировать подтверждение: {initResponse}");
    return;
}

Console.Write("Введите 4-значный код: ");
var otpCode = Console.ReadLine()?.Trim() ?? string.Empty;
if (otpCode.Length != 4 || !otpCode.All(char.IsDigit))
{
    Console.WriteLine("Неверный формат кода.");
    return;
}

var shutdownResponse = await SendCommandAsync($"EMERGENCY_SHUTDOWN:{otpCode}");
if (string.Equals(shutdownResponse, "SUCCESS", StringComparison.OrdinalIgnoreCase))
{
    RunCommand("sc.exe", $"stop {ServiceName}");
    RunCommand("sc.exe", $"config {ServiceName} start= disabled");
    RunCommand("taskkill", "/F /IM KidControl.UiHost.exe");

    Console.WriteLine("Система отключена.");
    await Task.Delay(TimeSpan.FromSeconds(3));
    return;
}

Console.WriteLine($"Отключение отклонено: {shutdownResponse}");

static bool IsRunningAsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static async Task<string> SendCommandAsync(string command)
{
    await using var client = new NamedPipeClientStream(
        ".",
        "KidControlCommandPipe",
        PipeDirection.InOut,
        PipeOptions.Asynchronous);

    await client.ConnectAsync(3000);

    await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
    using var reader = new StreamReader(client, Encoding.UTF8);

    await writer.WriteLineAsync(command);
    var response = await reader.ReadLineAsync();

    return string.IsNullOrWhiteSpace(response) ? "ERROR" : response.Trim();
}

static void RunCommand(string fileName, string arguments)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        process?.WaitForExit(5000);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Не удалось выполнить команду '{fileName} {arguments}': {ex.Message}");
    }
}
