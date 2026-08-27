namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// The self-update target the backend dictates in managed mode (RFC §9, hybrid updates):
/// "latest" (track newest, as standalone does) or a pinned tag like "v2.0.10". The fleet
/// policy applier writes it; the update loop reads it. Registered in both modes with the
/// default "latest", so a standalone agent (nothing sets it) behaves exactly as before.
/// Binaries still come from GitHub Releases — only the CHOICE of version moves to the backend.
/// </summary>
public sealed class FleetUpdateTarget
{
    private volatile string _target = "latest";

    public string Current => _target;

    public bool IsPinned => !string.Equals(_target, "latest", StringComparison.OrdinalIgnoreCase);

    public void Set(string? target)
    {
        if (!string.IsNullOrWhiteSpace(target))
            _target = target.Trim();
    }

    /// <summary>
    /// Whether a pinned target requires installing a different version than what's running.
    /// Compares on normalized version (drops a leading 'v' and any pre-release suffix), so
    /// "v2.0.10" vs running "2.0.10" is a no-op. Returns false for "latest".
    /// </summary>
    public static bool NeedsPinnedInstall(string currentVersionText, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, "latest", StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.Equals(Normalize(currentVersionText), Normalize(target), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V'))
            v = v[1..];
        var dash = v.IndexOf('-');
        return dash >= 0 ? v[..dash] : v;
    }
}
