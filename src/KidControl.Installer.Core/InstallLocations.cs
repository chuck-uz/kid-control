using KidControl.Contracts;

namespace KidControl.Installer.Core;

/// <summary>
/// Well-known install-time filesystem locations, derived once from
/// <see cref="KidControlNames.AppDataFolderName"/> so nothing is hardcoded.
/// Mirrors (but does not depend on) Infrastructure's AppPaths — Installer.Core
/// deliberately references only Contracts.
/// </summary>
public sealed class InstallLocations
{
    public InstallLocations(string? installDirectory = null, string? dataDirectory = null)
    {
        InstallDirectory = installDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            KidControlNames.AppDataFolderName);

        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            KidControlNames.AppDataFolderName);
    }

    /// <summary><c>C:\Program Files\KidControl</c> — the locked-down binary directory.</summary>
    public string InstallDirectory { get; }

    /// <summary><c>C:\ProgramData\KidControl</c> — protected runtime data + config + logs.</summary>
    public string DataDirectory { get; }

    public string ServiceExecutableName => KidControlNames.ServiceProcessName + ".exe";

    public string UiExecutableName => KidControlNames.UiExecutableName;

    public string ServiceExecutablePath => Path.Combine(InstallDirectory, ServiceExecutableName);

    public string UiExecutablePath => Path.Combine(InstallDirectory, UiExecutableName);

    public string AppSettingsPath => Path.Combine(DataDirectory, "appsettings.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
}
