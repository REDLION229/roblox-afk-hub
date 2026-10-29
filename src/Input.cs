using System;
using System.Runtime.InteropServices;
using System.Threading;
using static AfkHub.Native;

namespace AfkHub;

/// <summary>
/// Everything the app types or clicks goes through here as SendInput, so Windows tags it "injected"
/// and the <see cref="InputMonitor"/> never mistakes it for you using the PC.
/// </summary>
static class InputSender
{
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10,
        MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

    public static void Key(ushort vk, ushort scan, bool extended, bool up)
    {
        if (scan == 0) scan = (ushort)MapVirtualKey(vk, 0);
        uint flags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } });
    }

    public static void Tap(ushort vk, bool extended, int holdMs = 40)
    {
        Key(vk, 0, extended, false);
        Thread.Sleep(holdMs);
        Key(vk, 0, extended, true);
    }

    /// <summary>Moves the cursor to a physical screen point (works across monitors).</summary>
    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
        Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            (int)Math.Round((x - vx) * 65535.0 / vw), (int)Math.Round((y - vy) * 65535.0 / vh));
    }

    public static void Button(uint flag) => Mouse(flag);
    public static void Wheel(int delta) => Mouse(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta);

    public static void Click()
    {
        Button(MOUSEEVENTF_LEFTDOWN);
        Thread.Sleep(30);
        Button(MOUSEEVENTF_LEFTUP);
    }

    /// <summary>Windows only lets a process change the foreground window right after it sent input; a bare Alt tap satisfies that.</summary>
    public static void AltUnlock()
    {
        const ushort VK_MENU = 0x12;
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU } } });
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } });
    }

    public static bool BringToFront(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;
        if (IsIconic(hwnd)) { ShowWindow(hwnd, SW_RESTORE); Thread.Sleep(300); }
        for (int i = 0; i < 3 && GetForegroundWindow() != hwnd; i++)
        {
            AltUnlock();
            SetForegroundWindow(hwnd);
            Thread.Sleep(80);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>The window's client area (the game picture, without the title bar) in screen pixels.</summary>
    public static bool ClientRect(IntPtr hwnd, out RECT r)
    {
        r = default;
        if (!GetClientRect(hwnd, out var c)) return false;
        var p = new POINT();
        if (!ClientToScreen(hwnd, ref p)) return false;
        r = new RECT { Left = p.X, Top = p.Y, Right = p.X + c.Right, Bottom = p.Y + c.Bottom };
        return c.Right > 0 && c.Bottom > 0;
    }

    static void Mouse(uint flags, int dx = 0, int dy = 0, uint data = 0) =>
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, dx = dx, dy = dy, mouseData = data } } });

    static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}

/// <summary>
/// Low-level keyboard + mouse hooks on their own thread. Reports every event (for the recorder) and
/// keeps track of the last time a real person touched the PC, ignoring input the app injected itself.
/// Only runs while Macro mode or the recorder needs it.
/// </summary>
sealed class InputMonitor : IDisposable
{
    public delegate void KeyHandler(int vk, int scan, bool extended, bool down, bool injected);
    public delegate void MouseHandler(int message, int x, int y, int wheelDelta, bool injected);

    public event KeyHandler Key;
    public event MouseHandler Mouse;
    /// <summary>Raised (on the hook thread) whenever a real person presses a key, clicks, or moves the mouse.</summary>
    public event Action RealInput;

    volatile int _lastRealTick = Environment.TickCount;
    int _lastRealX = int.MinValue, _lastRealY;
    Thread _thread;
    uint _threadId;
    LowLevelProc _kbProc, _msProc; // kept alive so the GC doesn't collect them while hooked

    public bool Running => _thread != null;
    public int RealIdleMs => unchecked(Environment.TickCount - _lastRealTick);

    public void Start()
    {
        if (_thread != null) return;
        _lastRealTick = Environment.TickCount;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
            IntPtr mod = GetModuleHandle(null);
            IntPtr kb = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            IntPtr ms = SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (kb != IntPtr.Zero) UnhookWindowsHookEx(kb);
            if (ms != IntPtr.Zero) UnhookWindowsHookEx(ms);
        }) { IsBackground = true, Name = "input-hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(2000);
    }

    public void Stop()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool injected = (k.flags & LLKHF_INJECTED) != 0;
                if (!injected) MarkReal();
                Key?.Invoke((int)k.vkCode, (int)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, down, injected);
            }
            catch { /* never let an exception escape a hook */ }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                bool injected = (m.flags & LLMHF_INJECTED) != 0;
                if (!injected)
                {
                    // Ignore 1–2 px of sensor jitter so a mouse sitting on a desk doesn't count as "using the PC".
                    bool jitter = msg == WM_MOUSEMOVE && _lastRealX != int.MinValue &&
                                  Math.Abs(m.pt.X - _lastRealX) + Math.Abs(m.pt.Y - _lastRealY) < 4;
                    if (msg != WM_MOUSEMOVE || !jitter) { _lastRealX = m.pt.X; _lastRealY = m.pt.Y; }
                    if (!jitter) MarkReal();
                }
                int wheel = msg == WM_MOUSEWHEEL ? (short)(m.mouseData >> 16) : 0;
                Mouse?.Invoke(msg, m.pt.X, m.pt.Y, wheel, injected);
            }
            catch { }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    void MarkReal()
    {
        _lastRealTick = Environment.TickCount;
        RealInput?.Invoke();
    }

    public void Dispose() => Stop();
}
