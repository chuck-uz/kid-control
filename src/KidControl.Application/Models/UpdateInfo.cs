namespace KidControl.Application.Models;

/// <summary>A verified, newer release available for install.</summary>
public sealed record UpdateInfo(
    string Tag,
    Version Version,
    string ReleaseNotes,
    string AssetName,
    string DownloadUrl,
    long AssetSize,
    string? Sha256);

/// <summary>A release entry usable for rollback.</summary>
public sealed record ReleaseInfo(string Tag, Version Version, bool IsPrerelease);
