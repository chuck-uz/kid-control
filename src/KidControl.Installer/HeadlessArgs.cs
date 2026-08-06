using System.Globalization;
using KidControl.Installer.Core;

namespace KidControl.Installer;

/// <summary>
/// Turns command-line arguments into an <see cref="InstallSettings"/> for the silent
/// install path. Kept out of Program.cs so the entry point stays about wiring.
/// </summary>
internal static class HeadlessArgs
{
    public static InstallSettings ParseSettings(string[] args)
    {
        var token = GetOption(args, "--token")
            ?? throw new ArgumentException("Silent install requires --token <bot-token>.");

        var idsRaw = GetOption(args, "--admin-ids")
            ?? throw new ArgumentException("Silent install requires --admin-ids <id,id,…>.");

        var ids = ChatIds.Parse(idsRaw);
        if (ids.Count == 0)
        {
            throw new ArgumentException("--admin-ids did not contain any valid chat ids.");
        }

        return new InstallSettings
        {
            BotToken = token,
            AdminChatIds = ids,
            NightStart = ParseTime(GetOption(args, "--night-start"), TimeSpan.FromHours(22)),
            NightEnd = ParseTime(GetOption(args, "--night-end"), TimeSpan.FromHours(7)),
            TrustedThumbprint = GetOption(args, "--thumbprint"),
        };
    }

    private static TimeSpan ParseTime(string? value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

/// <summary>Parses a comma-separated list of Telegram chat ids into distinct longs.</summary>
internal static class ChatIds
{
    public static IReadOnlyList<long> Parse(string input) =>
        input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
}
