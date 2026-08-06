using System.Runtime.Versioning;

namespace KidControl.Installer.Core;

/// <summary>Copies payload binaries into the install directory and removes trees on uninstall.</summary>
public interface IPayloadDeployer
{
    /// <summary>Copies ServiceHost.exe + UiHost.exe from <paramref name="sourceDirectory"/> into place.</summary>
    void CopyBinaries(string sourceDirectory);

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
