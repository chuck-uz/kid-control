using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using KidControl.Contracts;
using Serilog;

namespace KidControl.UiHost.Services;

/// <summary>
/// The interactive-session sensor for the content monitor (RFC-05). Runs in the child's desktop
/// session and streams observations to the SYSTEM service over
/// <see cref="KidControlNames.MonitorEventsPipe"/>; the service does the matching (it holds the
/// lists + token) and pushes alerts. This first cut reports the active WINDOW TITLE (reliable,
/// low-risk — catches adult keywords, site/app names and profanity in titles); browser URL and
/// keystroke sources are added next. Disabled until the service sends MONITOR|on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorSensor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cts = new();
    private volatile bool _enabled;
    private Task? _loop;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private string _lastTitle = string.Empty;

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Enable/disable streaming (driven by the service's MONITOR verb).</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Log.Information("Content-monitor sensor {State}.", enabled ? "enabled" : "disabled");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_enabled)
                {
                    await PollOnceAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Monitor sensor poll error.");
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var title = Sanitize(GetWindowTitle(hwnd));
        if (title.Length == 0 || title == _lastTitle)
        {
            return; // nothing focused, or unchanged since last poll
        }
        _lastTitle = title;

        await SendAsync($"{MonitorEventProtocol.Window}{MonitorEventProtocol.Separator}{title}", ct).ConfigureAwait(false);
    }

    private async Task SendAsync(string line, CancellationToken ct)
    {
        try
        {
            if (_writer is null || _pipe is null || !_pipe.IsConnected)
            {
                await ConnectAsync(ct).ConfigureAwait(false);
            }
            if (_writer is null)
            {
                return;
            }

            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Monitor sensor send failed; will reconnect.");
            DisposePipe();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        DisposePipe();
        var pipe = new NamedPipeClientStream(".", KidControlNames.MonitorEventsPipe,
            PipeDirection.Out, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        _pipe = pipe;
        _writer = new StreamWriter(pipe) { AutoFlush = true };
    }

    private void DisposePipe()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _pipe = null;
    }

    /// <summary>Collapse control chars so a title can't break the line-based pipe protocol.</summary>
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) { return string.Empty; }
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        DisposePipe();
        _cts.Dispose();
    }

    // ─── Win32 ────────────────────────────────────────────────────────────────

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) { return string.Empty; }
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
