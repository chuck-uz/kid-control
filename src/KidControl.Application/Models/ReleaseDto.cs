namespace KidControl.Application.Models;

/// <summary>
/// Lightweight DTO for a release entry (used in rollback list).
/// </summary>
public sealed record ReleaseDto
{
    public required string Tag { get; init; }
    public required Version Version { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public bool IsPrerelease { get; init; }
}
