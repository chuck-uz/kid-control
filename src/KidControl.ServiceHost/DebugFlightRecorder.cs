using System.Text;

namespace KidControl.ServiceHost;

internal static class DebugFlightRecorder
{
    private const string PrimaryPath = @"C:\KidControl_Debug.txt";

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        try
        {
            File.AppendAllText(PrimaryPath, line, Encoding.UTF8);
            return;
        }
        catch
        {
            // fall through to temp
        }

        try
        {
            var fallbackPath = Path.Combine(Path.GetTempPath(), "KidControl_Debug.txt");
            File.AppendAllText(fallbackPath, line, Encoding.UTF8);
        }
        catch
        {
            // best-effort recorder
        }
    }
}

