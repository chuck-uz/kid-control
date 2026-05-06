namespace KidControl.Infrastructure.Configuration;

public sealed class UpdateConfig
{
    public const string SectionName = "UpdateConfig";

    /// <summary>GitHub repo in "owner/name" format, e.g. "chuck-uz/kid-control".</summary>
    public string Repository { get; set; } = "chuck-uz/kid-control";

    /// <summary>How often the background checker hits the GitHub API.</summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>When false, periodic checking is disabled (manual checks still work).</summary>
    public bool AutoCheckEnabled { get; set; } = true;

    /// <summary>Asset filename to download from a release. Always single-file installer.</summary>
    public string AssetName { get; set; } = "KidControl.Installer.exe";
}
