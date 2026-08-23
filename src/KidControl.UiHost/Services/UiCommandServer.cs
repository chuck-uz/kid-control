using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using KidControl.Contracts;
using Serilog;

namespace KidControl.UiHost.Services;

/// <summary>
/// Hosts the UI end of the service -> UI command pipe. Runs in the interactive desktop
/// session, so it can do what the SYSTEM service cannot: capture the screen and play audio.
/// Protocol is <see cref="UiCommandProtocol"/> — one request line, one response line.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiCommandServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private MediaPlayer? _player; // kept alive so playback isn't GC'd
    private bool _started;

    public void Start()
    {
        if (_started) { return; }
        _started = true;
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    KidControlNames.UiCommandPipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                await using var writer = new StreamWriter(server) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                var response = await HandleAsync(line).ConfigureAwait(false);
                await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "UiCommandServer loop error.");
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<string> HandleAsync(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return Err("empty request");
        }

        var sep = line.IndexOf(UiCommandProtocol.Separator);
        var verb = sep >= 0 ? line[..sep] : line;
        var arg = sep >= 0 ? line[(sep + 1)..] : string.Empty;

        try
        {
            switch (verb)
            {
                case UiCommandProtocol.Screenshot:
                    CaptureScreen(arg);
                    return UiCommandProtocol.Ok;
                case UiCommandProtocol.Play:
                    await PlayAsync(arg).ConfigureAwait(false);
                    return UiCommandProtocol.Ok;
                default:
                    return Err($"unknown verb '{verb}'");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UiCommandServer failed to handle '{Verb}'.", verb);
            return Err(ex.Message);
        }
    }

    private static string Err(string message) => $"{UiCommandProtocol.ErrorPrefix} {message}";

    // ─── Screenshot ───────────────────────────────────────────────────────────

    private static void CaptureScreen(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("screenshot path is empty");
        }

        // Virtual screen bounds in physical pixels (app is PerMonitorV2 DPI-aware).
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) { width = GetSystemMetrics(SM_CXSCREEN); height = GetSystemMetrics(SM_CYSCREEN); left = 0; top = 0; }

        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bmp.Save(path, ImageFormat.Png);
    }

    // ─── Audio playback ───────────────────────────────────────────────────────

    private Task PlayAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("audio file not found", path);
        }

        var app = Application.Current
            ?? throw new InvalidOperationException("no WPF application");

        // MediaPlayer must live on a Dispatcher thread; run on the UI thread and keep the
        // reference alive. NOTE: MediaPlayer uses Windows Media Foundation — MP3/WAV/MP4 play
        // out of the box; OGG/Opus (Telegram voice notes) need the free "Web Media Extensions".
        return app.Dispatcher.InvokeAsync(() =>
        {
            _player ??= new MediaPlayer();
            _player.Stop();
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();
        }).Task;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    // ─── Win32 ────────────────────────────────────────────────────────────────

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
