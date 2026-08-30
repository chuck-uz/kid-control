namespace KidControl.Contracts;

/// <summary>
/// Line protocol for the UI -> service content-monitor pipe
/// (<see cref="KidControlNames.MonitorEventsPipe"/>). The interactive UI (sensor) streams one
/// observation per line; the SYSTEM service matches it against the lists and pushes alerts.
///
/// Line: "&lt;KIND&gt;|&lt;text&gt;"  where KIND is KBD (keyboard buffer tail), WIN (active window
/// title) or URL (browser URL). The text may itself contain '|', so consumers split on the
/// FIRST separator only. No response is sent — it is a fire-and-forget stream.
/// </summary>
public static class MonitorEventProtocol
{
    public const char Separator = '|';

    /// <summary>Rolling tail of what the user is typing.</summary>
    public const string Keyboard = "KBD";

    /// <summary>Active window / application title.</summary>
    public const string Window = "WIN";

    /// <summary>Foreground browser URL.</summary>
    public const string Url = "URL";
}
