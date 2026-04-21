namespace KidControl.Installer.Controls;

/// <summary>
/// SystemFonts.MessageBoxFont is annotated nullable; use a stable fallback to avoid CS8602.
/// </summary>
internal static class InstallerFonts
{
    public static FontFamily MessageFontFamily { get; } =
        SystemFonts.MessageBoxFont?.FontFamily
        ?? SystemFonts.DefaultFont?.FontFamily
        ?? FontFamily.GenericSansSerif;
}
