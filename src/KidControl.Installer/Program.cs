namespace KidControl.Installer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Silent update/rollback mode: spawned by the running ServiceHost.
        // No UI is shown; exits when the update installation finishes.
        bool isSilent = args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            var exitCode = SilentInstall(args);
            Environment.Exit(exitCode);
            return;
        }

        Application.Run(new InstallerForm());
    }

    /// <returns>0 on success, non-zero on failure so SCM/callers can detect a failed update.</returns>
    private static int SilentInstall(string[] args)
    {
        try
        {
            var releaseTag = TryGetArgValue(args, "/tag");
            using var form = new InstallerForm();
            form.RunSilentUpdate(releaseTag);
            return 0;
        }
        catch (Exception ex)
        {
            // Write a log since there is no UI.
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "KidControl", "silent_install_error.log");
            try
            {
                System.IO.File.AppendAllText(logPath,
                    $"[{DateTimeOffset.UtcNow:u}] Silent install failed: {ex}\n");
            }
            catch { /* nothing we can do */ }

            return 1;
        }
    }

    private static string? TryGetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (a.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return a[(name.Length + 1)..];
            }
        }

        return null;
    }
}
