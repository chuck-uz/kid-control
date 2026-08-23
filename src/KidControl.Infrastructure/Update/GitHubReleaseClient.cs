using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KidControl.Application.Models;
using KidControl.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Typed HTTP client over the GitHub Releases REST API. Owner/repo come from
/// <see cref="UpdateConfig"/>; the base address, User-Agent (required by GitHub), and
/// timeout are configured on the injected <see cref="HttpClient"/> by DI.
///
/// Cleaned up from the original: no debug-file logging and no hardcoded machine paths.
/// The tag is URL-escaped in the by-tag path.
/// </summary>
public sealed class GitHubReleaseClient(
    HttpClient http,
    IOptions<UpdateConfig> config,
    ILogger<GitHubReleaseClient> logger)
{
    private readonly UpdateConfig _config = config.Value;

    private string RepoPath => $"repos/{_config.Owner}/{_config.Repository}";

    // Public repos: resolve/download via github.com (CDN + web redirect), which is NOT
    // subject to the api.github.com 60/hr unauthenticated rate limit that was returning
    // 403 and making the service think there were "no updates". Private repos keep using
    // the authenticated REST API (5000/hr).
    private bool UsePublicWeb => string.IsNullOrWhiteSpace(_config.GitHubToken);

    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        if (UsePublicWeb)
        {
            var tag = await ResolveLatestTagViaWebAsync(ct).ConfigureAwait(false);
            return tag is null ? null : BuildDirectInfo(tag);
        }

        var release = await GetJsonAsync<GitHubRelease>($"{RepoPath}/releases/latest", ct).ConfigureAwait(false);
        return ToUpdateInfo(release);
    }

    public async Task<UpdateInfo?> GetByTagAsync(string tag, CancellationToken ct = default)
    {
        if (UsePublicWeb)
        {
            return BuildDirectInfo(tag);
        }

        var escaped = Uri.EscapeDataString(tag);
        var release = await GetJsonAsync<GitHubRelease>($"{RepoPath}/releases/tags/{escaped}", ct).ConfigureAwait(false);
        return ToUpdateInfo(release);
    }

    /// <summary>Reads the latest tag from the github.com/.../releases/latest redirect (no API).</summary>
    private async Task<string?> ResolveLatestTagViaWebAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://github.com/{_config.Owner}/{_config.Repository}/releases/latest";
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
            var m = Regex.Match(finalUrl, @"/releases/tag/([^/?#]+)");
            if (m.Success)
            {
                return Uri.UnescapeDataString(m.Groups[1].Value);
            }

            logger.LogWarning("Could not resolve latest tag (final url: {Url}).", finalUrl);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve latest release tag via web redirect.");
            return null;
        }
    }

    /// <summary>Builds UpdateInfo with the direct CDN download URL — no API call.</summary>
    private UpdateInfo? BuildDirectInfo(string tag)
    {
        if (!TryParseVersion(tag, out var version))
        {
            return null;
        }

        var assetName = $"KidControl-Setup-{tag}.zip";
        var url = $"https://github.com/{_config.Owner}/{_config.Repository}/releases/download/{tag}/{assetName}";
        return new UpdateInfo(tag, version, string.Empty, assetName, url, AssetSize: 0, Sha256: null);
    }

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(int top, CancellationToken ct = default)
    {
        var perPage = Math.Clamp(top, 1, 100);
        var releases = await GetJsonAsync<List<GitHubRelease>>($"{RepoPath}/releases?per_page={perPage}", ct).ConfigureAwait(false);
        if (releases is null)
        {
            return [];
        }

        var result = new List<ReleaseInfo>(releases.Count);
        foreach (var release in releases)
        {
            if (release.Draft)
            {
                continue;
            }

            if (TryParseVersion(release.TagName, out var version))
            {
                result.Add(new ReleaseInfo(release.TagName, version, release.Prerelease));
            }
        }

        return result;
    }

    /// <summary>Streams an asset to <paramref name="destinationPath"/>; returns the bytes written.</summary>
    public async Task<long> DownloadAssetAsync(string assetUrl, string destinationPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        return output.Length;
    }

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var remaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var v)
                    ? string.Join(string.Empty, v) : "?";
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Length > 200) { body = body[..200]; }
                logger.LogWarning(
                    "GitHub {Url} returned HTTP {Status} (rate-limit-remaining={Remaining}). {Body}",
                    relativeUrl, (int)response.StatusCode, remaining, body);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GitHub request failed: {Url}", relativeUrl);
            return default;
        }
    }

    private static UpdateInfo? ToUpdateInfo(GitHubRelease? release)
    {
        if (release is null || release.Draft || !TryParseVersion(release.TagName, out var version))
        {
            return null;
        }

        var asset = SelectInstaller(release.Assets);
        if (asset is null)
        {
            return null;
        }

        return new UpdateInfo(
            Tag: release.TagName,
            Version: version,
            ReleaseNotes: release.Body ?? string.Empty,
            AssetName: asset.Name,
            DownloadUrl: asset.BrowserDownloadUrl,
            AssetSize: asset.Size,
            Sha256: null);
    }

    /// <summary>
    /// Prefers the self-contained setup <c>.zip</c> (installer + payload binaries in one
    /// archive), then a bare <c>.exe</c>, then the first asset. The zip is the artifact the
    /// self-update path can act on end-to-end, because a lone installer .exe carries no
    /// ServiceHost/UiHost payloads to deploy.
    /// </summary>
    private static GitHubReleaseAsset? SelectInstaller(List<GitHubReleaseAsset> assets)
        => assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
           ?? assets.Find(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
           ?? (assets.Count > 0 ? assets[0] : null);

    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            trimmed = trimmed[..dash];
        }

        return Version.TryParse(trimmed, out version!);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
