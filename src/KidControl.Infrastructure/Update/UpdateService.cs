using System.Diagnostics;
using System.Reflection;
using KidControl.Application.Interfaces;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Update;

public sealed class UpdateService : IUpdateService
{
    private static readonly string UpdateStagingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidControl", "updates");

    private readonly GitHubReleaseClient _client;
    private readonly UpdateMarkerService _marker;
    private readonly UpdateConfig _config;
    private readonly TelegramConfig _telegram;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        GitHubReleaseClient client,
        UpdateMarkerService marker,
        IOptions<UpdateConfig> config,
        IOptions<TelegramConfig> telegram,
        IHostApplicationLifetime lifetime,
        ILogger<UpdateService> logger)
    {
        _client = client;
        _marker = marker;
        _config = config.Value;
        _telegram = telegram.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    public async Task<UpdateInfoDto?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var release = await _client.GetLatestReleaseAsync(_config.Repository, ct).ConfigureAwait(false);
            if (release is null || release.Draft)
            {
                _logger.LogDebug("Update check: no release found.");
                return null;
            }

            if (!TryParseTag(release.TagName, out var releaseVersion))
            {
                _logger.LogWarning("Update check: cannot parse tag '{Tag}' as Version.", release.TagName);
                return null;
            }

            var current = GetCurrentVersion();
            if (releaseVersion <= current)
            {
                _logger.LogDebug("Update check: latest {Latest} <= current {Current}.", releaseVersion, current);
                return null;
            }

            var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, _config.AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                _logger.LogWarning("Update check: release {Tag} has no asset named '{AssetName}'.", release.TagName, _config.AssetName);
                return null;
            }

            return new UpdateInfoDto
            {
                Tag = release.TagName,
                Version = releaseVersion,
                AssetUrl = asset.BrowserDownloadUrl,
                AssetSize = asset.Size,
                ReleaseNotes = release.Body,
                PublishedAt = release.PublishedAt,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed.");
            return null;
        }
    }

    public async Task<IReadOnlyList<ReleaseDto>> GetAvailableVersionsForRollbackAsync(int top = 10, CancellationToken ct = default)
    {
        var releases = await _client.GetReleasesAsync(_config.Repository, top, ct).ConfigureAwait(false);
        var current = GetCurrentVersion();
        var result = new List<ReleaseDto>();
        foreach (var r in releases)
        {
            if (r.Draft) continue;
            if (!TryParseTag(r.TagName, out var v)) continue;
            if (v == current) continue;
            // Only releases that have the installer asset are usable for rollback.
            if (!r.Assets.Any(a => string.Equals(a.Name, _config.AssetName, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(new ReleaseDto
            {
                Tag = r.TagName,
                Version = v,
                PublishedAt = r.PublishedAt,
                IsPrerelease = r.Prerelease,
            });
        }
        return result;
    }

    public Task StartInstallAsync(string tag, CancellationToken ct = default)
        => DownloadAndLaunchAsync(tag, kind: "update", ct);

    public Task StartRollbackAsync(string tag, CancellationToken ct = default)
        => DownloadAndLaunchAsync(tag, kind: "rollback", ct);

    private async Task DownloadAndLaunchAsync(string tag, string kind, CancellationToken ct)
    {
        var release = await _client.GetReleaseByTagAsync(_config.Repository, tag, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Релиз с тегом {tag} не найден на GitHub.");
        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, _config.AssetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"В релизе {tag} нет ассета {_config.AssetName}.");

        var stageDir = Path.Combine(UpdateStagingRoot, tag);
        Directory.CreateDirectory(stageDir);
        var installerPath = Path.Combine(stageDir, _config.AssetName);

        _logger.LogInformation("Update: downloading {Tag} → {Path}", tag, installerPath);
        await _client.DownloadAssetAsync(asset.BrowserDownloadUrl, installerPath, progress: null, ct).ConfigureAwait(false);

        // Persist a marker so the new ServiceHost (post-restart) can announce success/failure.
        _marker.Write(new UpdateMarker
        {
            Kind = kind,
            TargetTag = tag,
            AdminChatIds = _telegram.AdminChatIds,
            StartedUtc = DateTimeOffset.UtcNow,
            PreviousVersion = GetCurrentVersion().ToString(),
        });

        // Spawn the installer. /silent means "no UI, no MessageBox". /update or /rollback both
        // trigger the same RunSilentUpdate() flow on the installer side.
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = kind == "rollback" ? "/silent /rollback" : "/silent /update",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stageDir,
        };

        try
        {
            Process.Start(startInfo);
            _logger.LogInformation("Update: installer spawned ({Kind} {Tag}). Stopping service for replacement.", kind, tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update: failed to spawn installer.");
            _marker.Delete();
            throw;
        }

        // Give the spawned process a head start before we exit the host.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            _lifetime.StopApplication();
        });
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var trimmed = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(trimmed, out version!);
    }
}
