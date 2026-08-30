using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Serilog;

namespace KidControl.UiHost.Services;

/// <summary>
/// A global low-level keyboard hook (WH_KEYBOARD_LL) that keeps a short in-memory rolling tail
/// of what is being typed, for the content monitor (RFC-05). The buffer is NEVER persisted or
/// logged — <see cref="MonitorSensor"/> reads <see cref="Snapshot"/> and streams it to the
/// SYSTEM service, which matches it and immediately discards it. An LL hook needs a message
/// loop on the installing thread, so this owns a dedicated background thread that installs the
/// hook and pumps messages until <see cref="Stop"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;
    private const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_TAB = 0x09;
    private const int MaxBuffer = 256;

    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private HookProc? _proc; // keep the delegate alive for the hook's lifetime
    private volatile bool _running;

    public void Start()
    {
        lock (_sync)
        {
            if (_running) { return; }
            _running = true;
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "kc-kbd-hook" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_sync)
        {
            if (!_running) { return; }
            _running = false;
            t = _thread;
            if (_threadId != 0) { PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
        }

        try { t?.Join(TimeSpan.FromSeconds(2)); } catch (Exception ex) { Log.Debug(ex, "kbd hook join"); }
        lock (_sync) { _buffer.Clear(); _thread = null; }
    }

    /// <summary>Current rolling tail of typed text (in-memory only).</summary>
    public string Snapshot()
    {
        lock (_sync) { return _buffer.ToString(); }
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Warning("Keyboard hook install failed (err {Err}).", Marshal.GetLastWin32Error());
            return;
        }

        Log.Information("Keyboard hook installed.");
        // Message loop — required for a low-level hook to receive callbacks.
        while (_running && GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // hook-only loop: nothing to dispatch
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Log.Information("Keyboard hook removed.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = (int)wParam;
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    Append((int)data.vkCode, data.scanCode);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Keyboard hook callback error.");
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Append(int vk, uint scan)
    {
        lock (_sync)
        {
            if (vk == VK_BACK)
            {
                if (_buffer.Length > 0) { _buffer.Remove(_buffer.Length - 1, 1); }
                return;
            }
            if (vk is VK_RETURN or VK_TAB)
            {
                AppendChar(' ');
                return;
            }

            var state = new byte[256];
            if (!GetKeyboardState(state)) { return; }
            var hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
            var sb = new StringBuilder(8);
            // wFlags = 4: do NOT change the kernel keyboard state, so we never disturb the user's
            // own typing / dead-key composition.
            var rc = ToUnicodeEx((uint)vk, scan, state, sb, sb.Capacity, 4, hkl);
            if (rc <= 0) { return; }

            foreach (var ch in sb.ToString())
            {
                if (!char.IsControl(ch)) { AppendChar(ch); }
            }
        }
    }

    private void AppendChar(char ch)
    {
        _buffer.Append(ch);
        if (_buffer.Length > MaxBuffer) { _buffer.Remove(0, _buffer.Length - MaxBuffer); }
    }

    // ─── Win32 ────────────────────────────────────────────────────────────────
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);
}
