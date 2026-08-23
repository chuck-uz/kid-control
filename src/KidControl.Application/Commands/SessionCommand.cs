using KidControl.Domain.ValueObjects;

namespace KidControl.Application.Commands;

/// <summary>
/// Parsed representation of an administrator intent. Parsing (text -> command) is
/// separated from execution (command -> effect), so the parser is a pure,
/// exhaustively testable function and the executor never touches strings.
/// </summary>
public abstract record SessionCommand
{
    public sealed record Status : SessionCommand;
    public sealed record Block : SessionCommand;
    public sealed record Unblock : SessionCommand;
    public sealed record AddTime(int Minutes) : SessionCommand;
    public sealed record SetRule(ScheduleRule Rule) : SessionCommand;
    public sealed record SetNight(NightWindow Window) : SessionCommand;

    /// <summary>Turns the whole night mode on or off (independent of the configured window).</summary>
    public sealed record SetNightEnabled(bool Enabled) : SessionCommand;
    public sealed record ResetTimer : SessionCommand;

    /// <summary>Turns the play/rest limit on or off (night block still applies when off).</summary>
    public sealed record SetIntervals(bool Enabled) : SessionCommand;

    public sealed record Pause : SessionCommand;
    public sealed record Resume : SessionCommand;
    public sealed record ShutdownPc : SessionCommand;
    public sealed record RestartPc : SessionCommand;

    /// <summary>Input that could not be understood; carries a help message.</summary>
    public sealed record Unknown(string Help) : SessionCommand;
}
