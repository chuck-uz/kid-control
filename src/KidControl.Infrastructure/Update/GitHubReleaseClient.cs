using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace KidControl.Infrastructure.Update;

/// <summary>
/// Thin HTTP wrapper around GitHub Releases REST API. No authentication required for
/// public repos (60 req/h rate limit). User-Agent header is mandatory by GitHub.
/// </summary>
public sealed class GitHubReleaseClient
{
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string repository, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repository}/releases/latest";
        return await _httpClient.GetFromJsonAsync<GitHubRelease>(url, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(string repository, int top = 10, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repository}/releases?per_page={top}";
        var list = await _httpClient.GetFromJsonAsync<List<GitHubRelease>>(url, ct).ConfigureAwait(false);
        return list ?? new List<GitHubRelease>();
    }

    public async Task<GitHubRelease?> GetReleaseByTagAsync(string repository, string tag, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repository}/releases/tags/{Uri.EscapeDataString(tag)}";
        return await _httpClient.GetFromJsonAsync<GitHubRelease>(url, ct).ConfigureAwait(false);
    }

    public async Task DownloadAssetAsync(string assetUrl, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var request = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        // GitHub asset binary stream — Accept must be octet-stream when using assets API.
        // Browser_download_url returns the binary directly; either works.
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var inputStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await outputStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (progress is not null && totalBytes > 0)
            {
                progress.Report((double)downloaded / totalBytes);
            }
        }
    }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}
