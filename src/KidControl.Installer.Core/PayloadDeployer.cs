using System.Runtime.Versioning;

namespace KidControl.Installer.Core;

/// <summary>Copies payload binaries into the install directory and removes trees on uninstall.</summary>
public interface IPayloadDeployer
{
    /// <summary>Copies ServiceHost.exe + UiHost.exe from <paramref name="sourceDirectory"/> into place.</summary>
    void CopyBinaries(string sourceDirectory);

    /// <summary>
    /// Copies the currently-installed binaries to a scratch backup directory and returns its
    /// path, so a failed update can be rolled back. A missing binary is simply skipped (a
    /// first-ever install has nothing to back up).
    /// </summary>
    string BackupBinaries();

    /// <summary>Restores binaries previously saved by <see cref="BackupBinaries"/> back into place.</summary>
    void RestoreBinaries(string backupDirectory);

    /// <summary>Deletes a backup directory once an update has succeeded. Best effort.</summary>
    void DiscardBackup(string backupDirectory);

    /// <summary>Deletes a directory tree with a few retries for transient locks. Best effort.</summary>
    bool DeleteDirectory(string path, Action<string>? progress = null);
}

/// <summary>
/// The only class in Installer.Core that moves files around. Isolating filesystem
/// deployment here keeps <see cref="InstallOrchestrator"/> a pure sequencer that is
/// trivial to unit test with a fake deployer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PayloadDeployer : IPayloadDeployer
{
    private readonly InstallLocations _locations;

    public PayloadDeployer(InstallLocations? locations = null) =>
        _locations = locations ?? new InstallLocations();

    public void CopyBinaries(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Payload source directory not found: {sourceDirectory}");
        }

        Directory.CreateDirectory(_locations.InstallDirectory);

        Copy(sourceDirectory, _locations.ServiceExecutableName, _locations.ServiceExecutablePath);
        Copy(sourceDirectory, _locations.UiExecutableName, _locations.UiExecutablePath);
    }

    // Backups live under the writable data tree (%ProgramData%), NOT the locked install dir,
    // so restore works even while the install directory ACLs are being flipped.
    private string BackupDirectory => Path.Combine(_locations.DataDirectory, "update", "backup");

    public string BackupBinaries()
    {
        var backupDir = BackupDirectory;
        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, recursive: true);
        }

        Directory.CreateDirectory(backupDir);
        BackupOne(_locations.ServiceExecutablePath, backupDir);
        BackupOne(_locations.UiExecutablePath, backupDir);
        return backupDir;
    }

    public void RestoreBinaries(string backupDirectory)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory) || !Directory.Exists(backupDirectory))
        {
            throw new DirectoryNotFoundException($"Backup directory not found: {backupDirectory}");
        }

        Directory.CreateDirectory(_locations.InstallDirectory);
        RestoreOne(backupDirectory, _locations.ServiceExecutableName, _locations.ServiceExecutablePath);
        RestoreOne(backupDirectory, _locations.UiExecutableName, _locations.UiExecutablePath);
    }

    public void DiscardBackup(string backupDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(backupDirectory) && Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a leftover backup is harmless; the next update overwrites it.
        }
    }

    private static void BackupOne(string sourcePath, string backupDir)
    {
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, Path.Combine(backupDir, Path.GetFileName(sourcePath)), overwrite: true);
        }
    }

    private static void RestoreOne(string backupDir, string fileName, string destinationPath)
    {
        var source = Path.Combine(backupDir, fileName);
        if (File.Exists(source))
        {
            File.Copy(source, destinationPath, overwrite: true);
        }
    }

    public bool DeleteDirectory(string path, Action<string>? progress = null)
    {
        const int attempts = 4;
        for (var i = 0; i < attempts; i++)
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == attempts - 1)
                {
                    progress?.Invoke($"Could not delete {path}: {ex.Message}");
                    return false;
                }

                progress?.Invoke($"Directory busy, retrying ({i + 1}/{attempts})…");
                Thread.Sleep(700);
            }
        }

        return !Directory.Exists(path);
    }

    private static void Copy(string sourceDirectory, string fileName, string destinationPath)
    {
        var source = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Payload binary missing: {source}", source);
        }

        File.Copy(source, destinationPath, overwrite: true);
    }
}
