using KidControl.Domain.ValueObjects;

namespace KidControl.Application.Commands;

/// <summary>
/// Pure parser: raw admin text -> <see cref="SessionCommand"/>. No I/O, no state.
/// Every branch is unit-testable in isolation.
/// </summary>
public static class CommandParser
{
    private const string Help =
        "Доступно: /status, /block, /unblock, /addtime [мин], /setrule [игра] [отдых], " +
        "/night [ЧЧ:ММ-ЧЧ:ММ], /reset, /pause, /resume, /shutdown, /restart.";

    public static SessionCommand Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new SessionCommand.Unknown(Help);
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = tokens[0].ToLowerInvariant();

        return verb switch
        {
            "/status" => new SessionCommand.Status(),
            "/block" => new SessionCommand.Block(),
            "/unblock" => new SessionCommand.Unblock(),
            "/reset" => new SessionCommand.ResetTimer(),
            "/pause" => new SessionCommand.Pause(),
            "/resume" => new SessionCommand.Resume(),
            "/shutdown" => new SessionCommand.ShutdownPc(),
            "/restart" => new SessionCommand.RestartPc(),
            "/addtime" => ParseAddTime(tokens),
            "/setrule" => ParseSetRule(tokens),
            "/night" => ParseNight(tokens),
            "/intervals" => ParseIntervals(tokens),
            _ => new SessionCommand.Unknown(Help)
        };
    }

    private static SessionCommand ParseAddTime(string[] tokens)
    {
        if (tokens.Length >= 2 && int.TryParse(tokens[1], out var minutes) && minutes > 0)
        {
            return new SessionCommand.AddTime(minutes);
        }

        return new SessionCommand.Unknown("Формат: /addtime [минуты], где минуты > 0.");
    }

    private static SessionCommand ParseSetRule(string[] tokens)
    {
        if (tokens.Length >= 3 &&
            int.TryParse(tokens[1], out var play) &&
            int.TryParse(tokens[2], out var rest) &&
            play > 0 && rest > 0)
        {
            return new SessionCommand.SetRule(new ScheduleRule(play, rest));
        }

        return new SessionCommand.Unknown("Формат: /setrule [игра_мин] [отдых_мин].");
    }

    private static SessionCommand ParseIntervals(string[] tokens)
    {
        if (tokens.Length >= 2)
        {
            var v = tokens[1].ToLowerInvariant();
            if (v is "on" or "вкл" or "1" or "true") return new SessionCommand.SetIntervals(true);
            if (v is "off" or "выкл" or "0" or "false") return new SessionCommand.SetIntervals(false);
        }

        return new SessionCommand.Unknown("Формат: /intervals on|off.");
    }

    private static SessionCommand ParseNight(string[] tokens)
    {
        if (tokens.Length >= 2)
        {
            var arg = tokens[1].ToLowerInvariant();
            if (arg is "on" or "вкл") return new SessionCommand.SetNightEnabled(true);
            if (arg is "off" or "выкл") return new SessionCommand.SetNightEnabled(false);
            if (NightWindow.TryParse(tokens[1], out var window)) return new SessionCommand.SetNight(window);
        }

        return new SessionCommand.Unknown("Формат: /night 22:00-07:00, либо /night on|off.");
    }
}
