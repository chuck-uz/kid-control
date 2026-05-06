namespace KidControl.Application.Models;

/// <summary>
/// Information about an available update discovered from GitHub releases.
/// </summary>
public sealed record UpdateInfoDto
{
    public required string Tag { get; init; }
    public required Version Version { get; init; }
    public required string AssetUrl { get; init; }
    public required long AssetSize { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}
