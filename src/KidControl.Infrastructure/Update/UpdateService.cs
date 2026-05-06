using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
    private int _updateLaunchStarted;

    #region agent log
    private static void AgentDebugLog(string runId, string hypothesisId, string location, string message, object data)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                sessionId = "9d75ca",
                runId,
                hypothesisId,
                location,
                message,
                data,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            var line = payload + Environment.NewLine;
            var paths = new[]
            {
                @"C:\kid-control\kid-control\debug-9d75ca.log",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "KidControl",
                    "debug-9d75ca.log"),
                Path.Combine(Path.GetTempPath(), "KidControl-debug-9d75ca.log")
            };

            foreach (var path in paths)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line);
                    return;
                }
                catch
                {
                    // Try the next path.
                }
            }
        }
        catch
        {
            // Debug logging must never affect the service.
        }
    }
    #endregion

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
        if (Interlocked.Exchange(ref _updateLaunchStarted, 1) == 1)
        {
            throw new InvalidOperationException("Обновление уже запущено. Дождитесь завершения установки и перезапуска службы.");
        }

        try
        {
            var release = await _client.GetReleaseByTagAsync(_config.Repository, tag, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Релиз с тегом {tag} не найден на GitHub.");
            var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, _config.AssetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"В релизе {tag} нет ассета {_config.AssetName}.");

            var runId = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var stageDir = Path.Combine(UpdateStagingRoot, tag, runId);
            Directory.CreateDirectory(stageDir);
            var installerPath = Path.Combine(stageDir, _config.AssetName);

            #region agent log
            AgentDebugLog("post-fix-update-lock", "H1,H2,H3,H4", "UpdateService.cs:DownloadAndLaunchAsync", "Update launch requested", new
            {
                tag,
                kind,
                stageDir,
                installerPath,
                installerExistsBeforeDownload = File.Exists(installerPath),
                installerLengthBeforeDownload = File.Exists(installerPath) ? new FileInfo(installerPath).Length : 0,
                assetUrlPresent = !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)
            });
            #endregion

            _logger.LogInformation("Update: downloading {Tag} → {Path}", tag, installerPath);
            await _client.DownloadAssetAsync(asset.BrowserDownloadUrl, installerPath, progress: null, ct).ConfigureAwait(false);

            #region agent log
            AgentDebugLog("post-fix-update-lock", "H1,H2,H3,H4", "UpdateService.cs:DownloadAndLaunchAsync", "Installer asset downloaded", new
            {
                installerPath,
                installerExistsAfterDownload = File.Exists(installerPath),
                installerLengthAfterDownload = File.Exists(installerPath) ? new FileInfo(installerPath).Length : 0
            });
            #endregion

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
                Arguments = kind == "rollback"
                    ? $"/silent /rollback /tag {tag}"
                    : $"/silent /update /tag {tag}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = stageDir,
            };

            var process = Process.Start(startInfo);
            #region agent log
            AgentDebugLog("post-fix-update-lock", "H4", "UpdateService.cs:DownloadAndLaunchAsync", "Installer process started", new
            {
                installerPath,
                processStarted = process is not null,
                processId = process?.Id,
                processHasExited = process?.HasExited
            });
            #endregion
            _logger.LogInformation("Update: installer spawned ({Kind} {Tag}). Stopping service for replacement.", kind, tag);

            // Give the spawned process a head start before we exit the host.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                _lifetime.StopApplication();
            });
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _updateLaunchStarted, 0);
            _marker.Delete();
            throw;
        }
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var trimmed = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(trimmed, out version!);
    }
}
