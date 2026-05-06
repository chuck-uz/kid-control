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
            SilentInstall(args);
            return;
        }

        Application.Run(new InstallerForm());
    }

    private static void SilentInstall(string[] args)
    {
        try
        {
            using var form = new InstallerForm();
            form.RunSilentUpdate();
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
        }
    }
}
