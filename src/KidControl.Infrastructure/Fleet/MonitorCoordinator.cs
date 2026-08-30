using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using KidControl.Contracts;
using KidControl.Domain.Monitoring;
using KidControl.Fleet.Contracts;
using KidControl.Infrastructure.Ipc;
using Microsoft.Extensions.Logging;

namespace KidControl.Infrastructure.Fleet;

/// <summary>
/// Service-side brain of the content monitor (RFC-05). The interactive UI is a thin sensor that
/// streams observations (keyboard tails, window titles, URLs) over the <see
/// cref="KidControlNames.MonitorEventsPipe"/>; this coordinator — running in the SYSTEM service,
/// which holds the fleet token and the lists — matches each observation with a
/// <see cref="ContentMonitor"/> and, on a hit, captures a screenshot (via the UI) and pushes the
/// alert to the backend. Matching lives here so the token and the (large) lists never leave the
/// privileged service. Raw observations cross only the local pipe and are never stored.
/// </summary>
public sealed class MonitorCoordinator(
    IUiCommandClient uiCommands,
    TimeProvider clock,
    ILogger<MonitorCoordinator> logger) : IDisposable
{
    // Don't grab a screenshot for every keystroke of the same word; one per key per 15s is plenty
    // (the backend applies its own 60s cooldown on top).
    private static readonly TimeSpan ScreenshotDedup = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private IFleetClient? _client;
    private volatile bool _enabled;
    private int _contextChars = 30;
    private int _listsVersion = -1;
    private ContentMonitor _monitor = ContentMonitor.Empty;

    /// <summary>Give the coordinator the reconciler's authenticated client and start the pipe
    /// server. Called once at startup; safe to call again (idempotent).</summary>
    public void AttachClient(IFleetClient client, PolicyDto? cachedPolicy)
    {
        _client = client;
        StartServer();
        if (cachedPolicy is not null)
        {
            _ = OnPolicyAsync(cachedPolicy, CancellationToken.None);
        }
    }

    /// <summary>Apply a policy: toggle on/off, and re-fetch the lists when their version changed.</summary>
    public async Task OnPolicyAsync(PolicyDto policy, CancellationToken ct = default)
    {
        _contextChars = policy.MonitorContextChars > 0 ? policy.MonitorContextChars : 30;
        _enabled = policy.WordMonitorEnabled;

        if (_client is not null && policy.MonitorListsVersion != _listsVersion)
        {
            var lists = await _client.GetMonitorListsAsync(ct).ConfigureAwait(false);
            if (lists is not null)
            {
                _monitor = new ContentMonitor(lists.Profanity, lists.AdultKeywords, lists.AdultDomains, lists.Exceptions);
                _listsVersion = lists.Version;
                logger.LogInformation("Monitor lists v{Version}: {Words} words, {Domains} domains, {Keywords} keywords.",
                    lists.Version, lists.Profanity.Count, lists.AdultDomains.Count, lists.AdultKeywords.Count);
            }
        }

        // Tell the UI sensor to start/stop hooking.
        try { await uiCommands.SetMonitorAsync(_enabled, ct).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "SetMonitor to UI failed (UI not running?)."); }
    }

    private void StartServer()
    {
        lock (_sync)
        {
            if (_serverTask is not null) { return; }
            _cts = new CancellationTokenSource();
            _serverTask = Task.Run(() => RunServerAsync(_cts.Token));
        }
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                while (!ct.IsCancellationRequested && server.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) { break; } // client disconnected
                    await HandleObservationAsync(line, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Monitor pipe server loop error.");
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        if (OperatingSystem.IsWindows())
        {
            // Interactive users (the child's session) may write; SYSTEM (this service) reads.
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                KidControlNames.MonitorEventsPipe, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        return new NamedPipeServerStream(
            KidControlNames.MonitorEventsPipe, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private async Task HandleObservationAsync(string line, CancellationToken ct)
    {
        if (!_enabled || _monitor.IsEmpty) { return; }

        var sep = line.IndexOf(MonitorEventProtocol.Separator);
        if (sep <= 0) { return; }
        var kind = line[..sep];
        var text = line[(sep + 1)..];
        if (text.Length == 0) { return; }

        var hit = kind switch
        {
            MonitorEventProtocol.Keyboard => _monitor.ScanText(text, MonitorSource.Keyboard, Tail(text, _contextChars * 2)),
            MonitorEventProtocol.Window => _monitor.ScanText(text, MonitorSource.Window, Trim(text, 150)),
            MonitorEventProtocol.Url => _monitor.ScanUrl(text),
            _ => null
        };
        if (hit is null) { return; }

        var key = $"{hit.Category}|{hit.Term}|{hit.Source}";
        var now = clock.GetUtcNow();
        lock (_sync)
        {
            if (_recent.TryGetValue(key, out var last) && now - last < ScreenshotDedup) { return; }
            _recent[key] = now;
        }

        await PushAsync(hit, ct).ConfigureAwait(false);
    }

    private async Task PushAsync(MonitorHit hit, CancellationToken ct)
    {
        var client = _client;
        if (client is null) { return; }

        byte[]? shot = null;
        try
        {
            var path = await uiCommands.CaptureScreenshotAsync(ct).ConfigureAwait(false);
            if (path is not null)
            {
                try { shot = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
                finally { try { File.Delete(path); } catch (Exception ex) { logger.LogDebug(ex, "Screenshot cleanup failed."); } }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Monitor screenshot capture failed."); }

        var dto = new WordAlertDto
        {
            Category = hit.Category == MonitorCategory.Adult ? "adult" : "profanity",
            Term = hit.Term,
            Source = hit.Source switch
            {
                MonitorSource.Keyboard => "keyboard",
                MonitorSource.Window => "window",
                _ => "url"
            },
            Context = Trim(hit.Context, _contextChars * 2 + 40)
        };

        await client.PostAlertAsync(dto, shot, ct).ConfigureAwait(false);
        logger.LogInformation("Monitor hit pushed: {Category} '{Term}' ({Source}).", dto.Category, dto.Term, dto.Source);
    }

    private static string Tail(string s, int n) => n <= 0 || s.Length <= n ? s : s[^n..];
    private static string Trim(string s, int n) => n <= 0 || s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (Exception ex) { logger.LogDebug(ex, "Monitor dispose."); }
        _cts?.Dispose();
    }
}
