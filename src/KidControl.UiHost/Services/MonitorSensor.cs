using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Automation;
using KidControl.Contracts;
using Serilog;

namespace KidControl.UiHost.Services;

/// <summary>
/// The interactive-session sensor for the content monitor (RFC-05). Runs in the child's desktop
/// session and streams observations to the SYSTEM service over
/// <see cref="KidControlNames.MonitorEventsPipe"/>; the service does the matching (it holds the
/// lists + token) and pushes alerts. Sources: the active WINDOW TITLE, the foreground browser
/// URL (via UI Automation), and the KEYBOARD (a short in-memory tail — never persisted). All
/// disabled until the service sends MONITOR|on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorSensor : IDisposable
{
    private static readonly TimeSpan WindowPoll = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan KeyboardPoll = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UrlTimeout = TimeSpan.FromMilliseconds(1200);

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandex"
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly KeyboardHook _hook = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _enabled;
    private Task? _windowLoop;
    private Task? _keyboardLoop;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private string _lastTitle = string.Empty;
    private string _lastUrl = string.Empty;
    private string _lastKbd = string.Empty;

    public void Start()
    {
        _windowLoop ??= Task.Run(() => RunWindowAsync(_cts.Token));
        _keyboardLoop ??= Task.Run(() => RunKeyboardAsync(_cts.Token));
    }

    /// <summary>Enable/disable all sources (driven by the service's MONITOR verb).</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            _hook.Start();
        }
        else
        {
            _hook.Stop();
            _lastTitle = _lastUrl = _lastKbd = string.Empty;
        }
        Log.Information("Content-monitor sensor {State}.", enabled ? "enabled" : "disabled");
    }

    // ─── Window title + browser URL ───────────────────────────────────────────
    private async Task RunWindowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_enabled) { await PollWindowAsync(ct).ConfigureAwait(false); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Monitor window poll error.");
            }

            try { await Task.Delay(WindowPoll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollWindowAsync(CancellationToken ct)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) { return; }

        var title = Sanitize(GetWindowTitle(hwnd));
        if (title.Length > 0 && title != _lastTitle)
        {
            _lastTitle = title;
            await SendAsync($"{MonitorEventProtocol.Window}{MonitorEventProtocol.Separator}{title}", ct).ConfigureAwait(false);
        }

        var url = await GetBrowserUrlAsync(hwnd, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(url) && url != _lastUrl)
        {
            _lastUrl = url;
            await SendAsync($"{MonitorEventProtocol.Url}{MonitorEventProtocol.Separator}{Sanitize(url)}", ct).ConfigureAwait(false);
        }
    }

    // ─── Keyboard ─────────────────────────────────────────────────────────────
    private async Task RunKeyboardAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_enabled)
                {
                    var snap = _hook.Snapshot();
                    if (snap.Length > 0 && snap != _lastKbd)
                    {
                        _lastKbd = snap;
                        await SendAsync($"{MonitorEventProtocol.Keyboard}{MonitorEventProtocol.Separator}{Sanitize(snap)}", ct)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Monitor keyboard poll error.");
            }

            try { await Task.Delay(KeyboardPoll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ─── Pipe ─────────────────────────────────────────────────────────────────
    private async Task SendAsync(string line, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writer is null || _pipe is null || !_pipe.IsConnected)
            {
                await ConnectAsync(ct).ConfigureAwait(false);
            }
            if (_writer is null) { return; }
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Monitor sensor send failed; will reconnect.");
            DisposePipe();
        }
        finally
        {
            _sendLock.Release();
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

    // ─── Browser URL via UI Automation (best-effort, timed out) ────────────────
    private async Task<string?> GetBrowserUrlAsync(IntPtr hwnd, CancellationToken ct)
    {
        try
        {
            var task = Task.Run(() => TryGetBrowserUrl(hwnd), ct);
            var done = await Task.WhenAny(task, Task.Delay(UrlTimeout, ct)).ConfigureAwait(false);
            return done == task ? await task.ConfigureAwait(false) : null; // abandon a slow UIA call
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Browser URL read error.");
            return null;
        }
    }

    private static string? TryGetBrowserUrl(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        string proc;
        try { proc = Process.GetProcessById((int)pid).ProcessName; }
        catch { return null; }
        if (!Browsers.Contains(proc)) { return null; }

        var element = AutomationElement.FromHandle(hwnd);
        // First Edit descendant is the address bar in Chromium/Firefox.
        var edit = element?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (edit is null) { return null; }

        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var patObj) && patObj is ValuePattern vp)
        {
            var val = vp.Current.Value;
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    /// <summary>Collapse control chars so text can't break the line-based pipe protocol.</summary>
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
        _hook.Stop();
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
