using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using KidControl.Application.Abstractions;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Self-update orchestration (<see cref="IUpdateService"/>). The install path runs as
/// SYSTEM, so it is defence-in-depth throughout:
///  * the asset host must be in the configured allow-list;
///  * the tag is sanitised before it becomes a staging path segment (no traversal);
///  * the downloaded length must equal the expected asset size;
///  * when required, the Authenticode signature + pinned thumbprint (and SHA-256, if the
///    release advertises one) are verified BEFORE the installer is ever executed;
///  * the service is only asked to stop once the installer process actually launched;
///  * staging is cleaned up on any failure.
/// </summary>
public sealed class UpdateService(
    GitHubReleaseClient client,
    AuthenticodeVerifier verifier,
    ISystemController system,
    IOptions<UpdateConfig> config,
    ILogger<UpdateService> logger) : IUpdateService
{
    private static readonly TimeSpan InstallerHeadStart = TimeSpan.FromSeconds(2);

    private readonly UpdateConfig _config = config.Value;
    private int _launchStarted;

    public Version CurrentVersion => ResolveCurrentVersion();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var latest = await client.GetLatestAsync(ct).ConfigureAwait(false);
            if (latest is null)
            {
                return null;
            }

            if (latest.Version <= CurrentVersion)
            {
                logger.LogDebug("Update check: latest {Latest} <= current {Current}.", latest.Version, CurrentVersion);
                return null;
            }

            return latest;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update check failed.");
            return null;
        }
    }

    public Task StartInstallAsync(string tag, CancellationToken ct = default)
        => DownloadVerifyLaunchAsync(tag, "update", ct);

    public Task StartRollbackAsync(string tag, CancellationToken ct = default)
        => DownloadVerifyLaunchAsync(tag, "rollback", ct);

    public async Task<IReadOnlyList<ReleaseInfo>> GetRollbackVersionsAsync(int top, CancellationToken ct = default)
    {
        var releases = await client.ListAsync(top, ct).ConfigureAwait(false);
        var current = CurrentVersion;
        return releases.Where(r => r.Version != current).ToList();
    }

    private async Task DownloadVerifyLaunchAsync(string tag, string kind, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _launchStarted, 1) == 1)
        {
            throw new InvalidOperationException("Обновление уже запущено.");
        }

        string? stageDir = null;
        try
        {
            var info = await client.GetByTagAsync(tag, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Релиз с тегом {tag} не найден.");

            EnsureHostAllowed(info.DownloadUrl);

            var safeTag = SanitizeTag(tag);
            var runId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            stageDir = Path.Combine(AppPaths.UpdateStagingRoot, safeTag, runId);
            Directory.CreateDirectory(stageDir);

            var assetName = SanitizeFileName(info.AssetName);
            var artifactPath = Path.Combine(stageDir, assetName);
            logger.LogInformation("Update: downloading {Tag} → {Path}", tag, artifactPath);

            var written = await client.DownloadAssetAsync(info.DownloadUrl, artifactPath, ct).ConfigureAwait(false);
            if (info.AssetSize > 0 && written != info.AssetSize)
            {
                throw new InvalidOperationException(
                    $"Downloaded size {written} does not match expected {info.AssetSize}.");
            }

            // Hash-pin the downloaded artifact (the .zip or .exe) before we open/run anything.
            await VerifyHashAsync(artifactPath, info, ct).ConfigureAwait(false);

            // Resolve the installer to run and the directory it copies payload binaries from.
            // A setup .zip carries the installer + ServiceHost/UiHost payloads; a bare .exe is
            // assumed to be a self-contained installer that already embeds them.
            string installerExe;
            string sourceDir;
            if (IsZip(assetName))
            {
                var extractDir = Path.Combine(stageDir, "extracted");
                ExtractZip(artifactPath, extractDir);
                installerExe = FindInstaller(extractDir)
                    ?? throw new InvalidOperationException(
                        "Setup archive does not contain KidControl.Installer.exe.");
                sourceDir = Path.GetDirectoryName(installerExe)!;
            }
            else
            {
                installerExe = artifactPath;
                sourceDir = stageDir;
                logger.LogWarning(
                    "Update asset is a bare .exe; assuming a self-contained installer with embedded payloads.");
            }

            // Signature-verify every executable we are about to trust — the installer AND the
            // payloads it will deploy — before launching anything as SYSTEM.
            VerifySignatures(sourceDir, installerExe);

            // The installer's headless binary-only update copies payloads from --source and
            // preserves appsettings.json + session_state.json. (Both update and rollback use
            // this path; only the tag whose asset we fetched differs.)
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerExe,
                Arguments = $"/update --source \"{sourceDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sourceDir
            });

            if (process is null)
            {
                throw new InvalidOperationException("Installer process failed to launch.");
            }

            logger.LogInformation("Update: installer launched ({Kind} {Tag}); scheduling service stop.", kind, tag);
            await Task.Delay(InstallerHeadStart, ct).ConfigureAwait(false);
            system.RequestServiceStop();
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _launchStarted, 0);
            CleanupStaging(stageDir);
            throw;
        }
    }

    private async Task VerifyHashAsync(string artifactPath, UpdateInfo info, CancellationToken ct)
    {
        if (info.Sha256 is not { Length: > 0 })
        {
            return;
        }

        var actual = await verifier.ComputeSha256Async(artifactPath, ct).ConfigureAwait(false);
        var expected = info.Sha256.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Downloaded asset SHA-256 does not match the expected hash.");
        }
    }

    private void VerifySignatures(string sourceDir, string installerExe)
    {
        if (!_config.RequireSignature)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Signature verification is required but only supported on Windows.");
        }

        // The installer plus every KidControl executable it will deploy from the source dir.
        var toVerify = new List<string> { installerExe };
        foreach (var exe in Directory.EnumerateFiles(sourceDir, "KidControl*.exe", SearchOption.AllDirectories))
        {
            if (!toVerify.Contains(exe, StringComparer.OrdinalIgnoreCase))
            {
                toVerify.Add(exe);
            }
        }

        foreach (var exe in toVerify)
        {
            if (!verifier.VerifySignature(exe, _config.TrustedThumbprint))
            {
                throw new InvalidOperationException(
                    $"Signature/thumbprint verification failed for {Path.GetFileName(exe)}.");
            }
        }
    }

    private static bool IsZip(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    internal static string? FindInstaller(string extractDir) =>
        Directory.EnumerateFiles(extractDir, "KidControl.Installer.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

    /// <summary>Extracts a zip with an explicit zip-slip guard (entries may not escape the target).</summary>
    internal static void ExtractZip(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var destRoot = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!targetPath.StartsWith(destRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Zip entry escapes the target directory: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath); // directory entry
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private void EnsureHostAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid asset URL: {url}");
        }

        var allowed = _config.AllowedAssetHosts.Any(
            h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidOperationException($"Asset host '{uri.Host}' is not in the allow-list.");
        }
    }

    private static string SanitizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            tag.Contains("..", StringComparison.Ordinal) ||
            tag.IndexOfAny(['/', '\\']) >= 0 ||
            tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Unsafe release tag: '{tag}'.");
        }

        return tag;
    }

    private static string SanitizeFileName(string name)
    {
        var stripped = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(stripped) || stripped.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Unsafe asset name: '{name}'.");
        }

        return stripped;
    }

    private void CleanupStaging(string? stageDir)
    {
        if (string.IsNullOrEmpty(stageDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(stageDir))
            {
                Directory.Delete(stageDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to clean up staging directory {Path}.", stageDir);
        }
    }

    private Version ResolveCurrentVersion()
    {
        // Single-file hosts report Assembly.GetName().Version as 0.0.0.0; MinVer stamps the
        // real version into the executable's ProductVersion, so prefer that.
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (TryParseLabel(info.ProductVersion, out var fromProduct))
                {
                    return fromProduct;
                }

                if (TryParseLabel(info.FileVersion, out var fromFile))
                {
                    return fromFile;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read version from host executable.");
        }

        return Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private static bool TryParseLabel(string? label, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var s = label.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            s = s[..dash];
        }

        return Version.TryParse(s, out version!);
    }
}
