namespace KidControl.Contracts;

/// <summary>
/// Line protocol for the service -> UI command pipe (<see cref="KidControlNames.UiCommandPipe"/>).
/// The UI process (interactive session) hosts the server; the service connects as a client to
/// ask it to do things only a desktop-session process can: capture the screen, play audio.
///
/// Request:  "&lt;VERB&gt;|&lt;absolute-path&gt;"   Response: "OK" or "ERR &lt;message&gt;"
/// </summary>
public static class UiCommandProtocol
{
    public const char Separator = '|';

    /// <summary>SCREENSHOT|&lt;pngPath&gt; — capture the screen(s) into the PNG at that path.</summary>
    public const string Screenshot = "SCREENSHOT";

    /// <summary>PLAY|&lt;audioPath&gt; — play the audio file.</summary>
    public const string Play = "PLAY";

    public const string Ok = "OK";
    public const string ErrorPrefix = "ERR";
}
