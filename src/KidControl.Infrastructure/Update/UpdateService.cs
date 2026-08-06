using System.Diagnostics;
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

            var installerPath = Path.Combine(stageDir, SanitizeFileName(info.AssetName));
            logger.LogInformation("Update: downloading {Tag} → {Path}", tag, installerPath);

            var written = await client.DownloadAssetAsync(info.DownloadUrl, installerPath, ct).ConfigureAwait(false);
            if (info.AssetSize > 0 && written != info.AssetSize)
            {
                throw new InvalidOperationException(
                    $"Downloaded size {written} does not match expected {info.AssetSize}.");
            }

            await VerifyBeforeExecuteAsync(installerPath, info, ct).ConfigureAwait(false);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = kind == "rollback" ? $"/silent /rollback /tag {tag}" : $"/silent /update /tag {tag}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = stageDir
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

    private async Task VerifyBeforeExecuteAsync(string installerPath, UpdateInfo info, CancellationToken ct)
    {
        if (info.Sha256 is { Length: > 0 })
        {
            var actual = await verifier.ComputeSha256Async(installerPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, info.Sha256.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Downloaded installer SHA-256 does not match the expected hash.");
            }
        }

        if (!_config.RequireSignature)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Signature verification is required but only supported on Windows.");
        }

        if (!verifier.VerifySignature(installerPath, _config.TrustedThumbprint))
        {
            throw new InvalidOperationException("Installer signature/thumbprint verification failed.");
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
