using KidControl.Contracts;

namespace KidControl.Infrastructure.Configuration;

/// <summary>Centralised, well-known filesystem locations under protected %ProgramData%.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        KidControlNames.AppDataFolderName);

    public static string StateFile => Path.Combine(Root, "session_state.json");
    public static string OverrideConfigFile => Path.Combine(Root, "appsettings.json");
    public static string UpdateStagingRoot => Path.Combine(Root, "updates");
    public static string LogsRoot => Path.Combine(Root, "logs");
}
