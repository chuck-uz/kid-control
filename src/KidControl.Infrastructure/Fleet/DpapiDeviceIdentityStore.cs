using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Stores the device identity as a DPAPI-encrypted blob at
/// <c>%ProgramData%\KidControl\device_identity.dat</c>. Two layers protect it: the app-data
/// root already carries a DACL the child can't touch, and DPAPI machine-scope encryption
/// means the file is useless if copied off the machine. The token never hits disk in clear
/// text and is never logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiDeviceIdentityStore(ILogger<DpapiDeviceIdentityStore> logger) : IDeviceIdentityStore
{
    // Ties the ciphertext to this app so an unrelated LocalMachine secret can't be swapped in.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KidControl.Fleet.DeviceIdentity.v1");

    private static string FilePath => Path.Combine(AppPaths.Root, "device_identity.dat");

    public bool IsEnrolled => Load() is not null;

    public DeviceIdentity? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var cipher = File.ReadAllBytes(FilePath);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            var json = Encoding.UTF8.GetString(plain);
            return FleetJson.Deserialize<DeviceIdentity>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Corrupt/unreadable identity: treat as not-enrolled rather than crashing the service.
            logger.LogWarning(ex, "Could not read device identity; treating as not enrolled.");
            return null;
        }
    }

    public void Save(DeviceIdentity identity)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var json = FleetJson.Serialize(identity);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.LocalMachine);

        // Write-then-replace so a crash mid-write can't leave a half-written identity.
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, FilePath, overwrite: true);
        logger.LogInformation("Saved device identity for device {DeviceId}.", identity.DeviceId);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete device identity file.");
        }
    }
}
