using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using KidControl.Installer.Core;

namespace KidControl.Installer;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Entry point. Three modes:
    ///   • <c>/silent</c>  — headless full install driven entirely from CLI args (no window).
    ///   • <c>/update</c>  — headless binary-only update (config + state preserved).
    ///   • (default)       — the interactive WinForms wizard.
    /// The headless modes exist so the self-update path (ServiceHost → installer) never
    /// has to construct a Form, which was the core architectural flaw in v1.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = HasFlag(args, "/silent");
        var update = HasFlag(args, "/update");

        if (silent || update)
        {
            return RunHeadless(args, isUpdate: update);
        }

        if (!IsElevated())
        {
            // The manifest requests elevation, so this is a rare fallback (e.g. launched
            // with a stripped token). Attempt a one-shot elevated relaunch.
            if (TryRelaunchElevated(args, out var code))
            {
                return code;
            }

            MessageBox.Show(
                "KidControl Installer must be run as Administrator.",
                "Administrator required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return 0;
    }

    private static int RunHeadless(string[] args, bool isUpdate)
    {
        if (!IsElevated())
        {
            // Relaunch elevated preserving the original args; return the child's exit code.
            return TryRelaunchElevated(args, out var code)
                ? code
                : Fail("Administrator privileges are required for a headless install/update.");
        }

        try
        {
            var orchestrator = InstallOrchestrator.CreateDefault();
            var source = GetOption(args, "--source") ?? AppContext.BaseDirectory;

            void Progress(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

            if (isUpdate)
            {
                orchestrator.Update(new UpdateRequest { SourceDirectory = source }, Progress);
            }
            else
            {
                var settings = HeadlessArgs.ParseSettings(args);
                orchestrator.Install(
                    new InstallRequest { SourceDirectory = source, Settings = settings },
                    Progress);
            }

            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return 1;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool TryRelaunchElevated(string[] args, out int exitCode)
    {
        exitCode = 1;
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = string.Join(' ', args.Select(Quote)),
                UseShellExecute = true,
                Verb = "runas", // triggers the UAC prompt
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            exitCode = process.ExitCode;
            return true;
        }
        catch
        {
            // User declined the UAC prompt, or elevation is unavailable.
            return false;
        }
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
