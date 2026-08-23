namespace KidControl.Contracts;

/// <summary>
/// A shared drop folder both the SYSTEM service and the interactive-user UI can read/write,
/// used to hand screenshots and audio between them. %PUBLIC% (C:\Users\Public) is chosen
/// because its default ACL grants both SYSTEM and interactive users modify rights — unlike
/// the SYSTEM-only %ProgramData%\KidControl tree.
/// </summary>
public static class TransferPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
        KidControlNames.AppDataFolderName,
        "transfer");

    /// <summary>Returns a fresh unique path with the given extension, creating the folder.</summary>
    public static string NewFile(string extension)
    {
        Directory.CreateDirectory(Root);
        var name = Guid.NewGuid().ToString("N") + "." + extension.TrimStart('.');
        return Path.Combine(Root, name);
    }
}
