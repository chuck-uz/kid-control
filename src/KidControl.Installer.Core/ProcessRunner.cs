using System.Diagnostics;

namespace KidControl.Installer.Core;

/// <summary>Outcome of an external process invocation.</summary>
public readonly record struct ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>Best single-line diagnostic (stderr preferred, else stdout).</summary>
    public string Detail
    {
        get
        {
            var err = StandardError.Trim();
            return err.Length > 0 ? err : StandardOutput.Trim();
        }
    }
}

/// <summary>
/// Runs a console helper (sc.exe, schtasks.exe, …) and returns its result.
///
/// The original God-class read stdout and stderr sequentially after WaitForExit,
/// which deadlocks whenever a child fills one pipe buffer while we block on the
/// other. Here both streams are drained concurrently via async reads BEFORE the
/// wait completes, so a chatty child can never wedge the installer.
/// </summary>
internal static class ProcessRunner
{
    public static ProcessResult Run(string fileName, string arguments, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();

        // Start draining immediately — do not wait first, or a full pipe deadlocks the child.
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort — the caller decides whether a timeout is fatal.
            }

            return new ProcessResult(-1, Drain(stdOut), Drain(stdErr), TimedOut: true);
        }

        // The int overload returns once the process exits but does not guarantee the
        // async output has flushed; the parameterless overload does.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, Drain(stdOut), Drain(stdErr), TimedOut: false);
    }

    private static string Drain(Task<string> readTask)
    {
        try
        {
            return readTask.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }
}
