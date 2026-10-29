using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using static AfkHub.Native;

namespace AfkHub;

enum MacroState
{
    Off,          // nothing assigned to run
    WaitingWindow,// the macro's window is closed, disconnected or still loading (e.g. during auto-rejoin)
    WaitingFocus, // couldn't bring the window to the front; retrying
    Playing,
    Yielding,     // anti-AFK asked for focus; finishing the current loop first
    Yielded,      // keys released, anti-AFK is using the other windows
    PausedUser,   // a real person touched the PC
    Finished,     // played once (looping off)
    TurnDone,     // played its loops for this turn; keys released, waiting for the next window's turn
}

/// <summary>
/// Plays one macro in one Roblox window, on its own thread. Only one window can run a macro at a time,
/// because Roblox only takes input from the window in front.
/// </summary>
sealed class MacroEngine : IDisposable
{
    readonly AutoResetEvent _wake = new(false);
    readonly HashSet<int> _held = new(); // virtual keys / mouse buttons currently held down by the macro
    readonly Thread _thread;
    volatile bool _quit;

    volatile RobloxInstance _window;
    volatile MacroFile _macro;
    double _speed = 1;
    bool _loop = true, _pin = true;
    Func<int> _windowFps = () => 60;
    RECT? _pinRect;
    MacroAssignment _assignment;

    /// <summary>The window was moved on purpose (Arrange windows): pin it at its new spot instead of snapping it back.</summary>
    public void MovePin(RobloxInstance w, RECT r)
    {
        lock (_held)
        {
            if (_window != w || _pinRect == null) return;
            _pinRect = r;
            if (_assignment != null) _assignment.PinnedRect = $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
        }
    }
    int _generation; // bumped on every Start/Stop so a running loop knows it's stale
    volatile int _turnLength; // loops per turn; 0 = keep looping

    volatile bool _yieldRequested, _userPause;
    DateTime _yieldRequestedUtc;

    public MacroState State { get; private set; } = MacroState.Off;
    public RobloxInstance Window => _window;
    public MacroFile Macro => _macro;
    public int Loops { get; private set; }
    public DateTime? StartedUtc { get; private set; }
    public string LastProblem { get; private set; }

    public event Action<string, Level> Log;

    public MacroEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "macro-player", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>True while a macro is assigned and wants the foreground (anything but Off/Finished).</summary>
    public bool Active => State is not (MacroState.Off or MacroState.Finished);
    /// <summary>True once the macro has let go of the keyboard and mouse for anti-AFK (or isn't using them).</summary>
    public bool Yielded => State is MacroState.Yielded or MacroState.Off or MacroState.Finished or MacroState.PausedUser
        or MacroState.WaitingWindow or MacroState.TurnDone;

    /// <summary>Loops to play before this window's turn ends (0 = loop until stopped).</summary>
    public void SetTurnLength(int loops) => _turnLength = Math.Max(0, loops);
    public int TurnLength => _turnLength;

    public void Start(RobloxInstance w, MacroFile m, MacroAssignment a, bool pin, Func<int> windowFps, int turnLength = 0)
    {
        _turnLength = Math.Max(0, turnLength);
        lock (_held)
        {
            _generation++;
            _window = w;
            _macro = m;
            _speed = Math.Max(0.25, Math.Min(4, a.Speed <= 0 ? 1 : a.Speed));
            _loop = a.Loop;
            _pin = pin;
            _windowFps = windowFps;
            _pinRect = ParseRect(a.PinnedRect);
            if (_pinRect == null && pin && GetWindowRect(w.Hwnd, out var r) && !IsIconic(w.Hwnd))
            {
                _pinRect = r;
                a.PinnedRect = $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
            }
            _userPause = false;
            _assignment = a;
            Loops = 0;
            StartedUtc = DateTime.UtcNow;
            LastProblem = null;
            State = MacroState.Playing;
        }
        _wake.Set();
    }

    public void Stop()
    {
        lock (_held)
        {
            _generation++;
            _window = null;
            _macro = null;
            _yieldRequested = false;
            _userPause = false;
            StartedUtc = null;
            State = MacroState.Off;
        }
        _wake.Set();
    }

    /// <summary>The macro's window went away (closed, disconnected, rejoining). Keep the assignment, stop sending input.</summary>
    public void Detach()
    {
        if (_window == null) return;
        lock (_held) { _generation++; _window = null; State = MacroState.WaitingWindow; }
        _wake.Set();
    }

    public void RequestYield()
    {
        if (_yieldRequested) return;
        _yieldRequestedUtc = DateTime.UtcNow;
        _yieldRequested = true;
        _wake.Set();
    }

    public void EndYield()
    {
        if (!_yieldRequested) return;
        _yieldRequested = false;
        _wake.Set();
    }

    /// <summary>Called from the input hook thread when a real person touches the PC.</summary>
    public void PauseForUser()
    {
        if (_userPause || State is not (MacroState.Playing or MacroState.Yielding or MacroState.WaitingFocus)) return;
        _userPause = true;
        _wake.Set();
    }

    public bool PausedForUser => _userPause;

    public void ResumeFromUser()
    {
        if (!_userPause) return;
        _userPause = false;
        _wake.Set();
    }

    // ---------------------------------------------------------------- player thread

    void Run()
    {
        while (!_quit)
        {
            var w = _window;
            var m = _macro;
            int gen = _generation;

            if (w == null || m == null || State is MacroState.Off or MacroState.Finished or MacroState.WaitingWindow or MacroState.TurnDone)
            {
                _wake.WaitOne(250);
                continue;
            }
            if (_yieldRequested) { SetState(MacroState.Yielded, gen); _wake.WaitOne(150); continue; }
            if (_userPause) { SetState(MacroState.PausedUser, gen); _wake.WaitOne(250); continue; }
            if (!IsWindow(w.Hwnd)) { SetState(MacroState.WaitingWindow, gen); continue; }

            if (_pin && _pinRect is { } pr && GetWindowRect(w.Hwnd, out var cur) && !IsIconic(w.Hwnd) &&
                (cur.Left != pr.Left || cur.Top != pr.Top || cur.Right != pr.Right || cur.Bottom != pr.Bottom))
                SetWindowPos(w.Hwnd, IntPtr.Zero, pr.Left, pr.Top, pr.Right - pr.Left, pr.Bottom - pr.Top, SWP_NOZORDER | SWP_NOACTIVATE);

            if (!InputSender.BringToFront(w.Hwnd))
            {
                if (State != MacroState.WaitingFocus) LastProblem = "Couldn't bring the window to the front. Is the PC locked?";
                SetState(MacroState.WaitingFocus, gen);
                _wake.WaitOne(1500);
                continue;
            }

            SetState(MacroState.Playing, gen);
            Thread.Sleep(150); // let Roblox register the focus before the first input
            var result = PlayOnce(w, m, gen);
            ReleaseAll();

            if (gen != _generation) continue;
            switch (result)
            {
                case LoopEnd.Completed:
                    Loops++;
                    if (!_loop) { SetState(MacroState.Finished, gen); Log?.Invoke($"Macro \"{m.Name}\" finished.", Level.Good); }
                    else if (_turnLength > 0 && Loops >= _turnLength) SetState(MacroState.TurnDone, gen);
                    break;
                case LoopEnd.FocusLost when !_userPause && !_yieldRequested:
                    LastProblem = "Another window took focus; the macro restarts its loop.";
                    _wake.WaitOne(500);
                    break;
            }
        }
    }

    enum LoopEnd { Completed, Stopped, FocusLost, Yield, User }

    LoopEnd PlayOnce(RobloxInstance w, MacroFile m, int gen)
    {
        // Roblox only samples input once per frame: at 5 FPS a 40 ms tap can fall between frames.
        // Hold every key/button for at least ~2.5 frames of the window's FPS.
        int fps = Math.Max(1, _windowFps());
        int minHold = Math.Max(35, Math.Min(600, (int)(2500.0 / fps)));
        int frame = Math.Max(16, 1000 / fps);
        var downAt = new Dictionary<int, double>();

        timeBeginPeriod(1);
        try
        {
            var clock = Stopwatch.StartNew();
            double last = 0;
            int lastX = int.MinValue, lastY = int.MinValue;

            foreach (var e in m.Events)
            {
                int type = e[1];
                double target = Math.Max(last, e[0] / _speed);
                int code = HeldCode(e);
                if (MacroFile.IsUp(type) && downAt.TryGetValue(code, out var d)) target = Math.Max(target, d + minHold);

                // Wait until it's time for this event, checking for interruptions every few ms.
                while (true)
                {
                    if (_quit || gen != _generation) return LoopEnd.Stopped;
                    if (_userPause) return LoopEnd.User;
                    if (GetForegroundWindow() != w.Hwnd) return LoopEnd.FocusLost;
                    if (_yieldRequested)
                    {
                        // Prefer the end of the loop; otherwise a moment with nothing held; never wait more than 30 s.
                        double waited = (DateTime.UtcNow - _yieldRequestedUtc).TotalSeconds;
                        if ((waited > 8 && _held.Count == 0) || waited > 30) { SetState(MacroState.Yielded, gen); return LoopEnd.Yield; }
                        SetState(MacroState.Yielding, gen);
                    }
                    double wait = target - clock.Elapsed.TotalMilliseconds;
                    if (wait <= 0) break;
                    Thread.Sleep((int)Math.Min(10, Math.Max(1, wait)));
                }

                if (MacroFile.IsMouse(type) && Point(w, m, e, out var x, out var y))
                {
                    if (x != lastX || y != lastY)
                    {
                        InputSender.MoveTo(x, y);
                        lastX = x; lastY = y;
                        // Give a low-FPS window a frame to see the new cursor position before a click.
                        if (MacroFile.IsDown(type)) Thread.Sleep(Math.Min(frame, 250));
                    }
                }

                Fire(e);
                if (MacroFile.IsDown(type)) { downAt[code] = clock.Elapsed.TotalMilliseconds; lock (_held) _held.Add(code); }
                if (MacroFile.IsUp(type)) { downAt.Remove(code); lock (_held) _held.Remove(code); }
                last = clock.Elapsed.TotalMilliseconds;
            }

            // Honour the recording's tail (time after the last action) before looping.
            double end = Math.Max(last, m.DurationMs / _speed);
            while (clock.Elapsed.TotalMilliseconds < end)
            {
                if (_quit || gen != _generation) return LoopEnd.Stopped;
                if (_userPause) return LoopEnd.User;
                if (_yieldRequested) break;
                Thread.Sleep(10);
            }
            return LoopEnd.Completed;
        }
        finally { timeEndPeriod(1); }
    }

    static int HeldCode(int[] e) => e[1] switch
    {
        MacroFile.KeyDown or MacroFile.KeyUp => e[2],
        MacroFile.LeftDown or MacroFile.LeftUp => -1,
        MacroFile.RightDown or MacroFile.RightUp => -2,
        MacroFile.MiddleDown or MacroFile.MiddleUp => -3,
        _ => int.MinValue,
    };

    static bool Point(RobloxInstance w, MacroFile m, int[] e, out int x, out int y)
    {
        x = e[2]; y = e[3];
        if (m.Coords != "window") return true;
        if (!InputSender.ClientRect(w.Hwnd, out var r)) return false;
        x = r.Left + (int)Math.Round(e[2] * (r.Right - r.Left) / (double)MacroFile.Scale);
        y = r.Top + (int)Math.Round(e[3] * (r.Bottom - r.Top) / (double)MacroFile.Scale);
        return true;
    }

    static void Fire(int[] e)
    {
        switch (e[1])
        {
            case MacroFile.KeyDown: InputSender.Key((ushort)e[2], (ushort)(e[3] & 0xFFFF), (e[3] & 0x10000) != 0, false); break;
            case MacroFile.KeyUp: InputSender.Key((ushort)e[2], (ushort)(e[3] & 0xFFFF), (e[3] & 0x10000) != 0, true); break;
            case MacroFile.LeftDown: InputSender.Button(MOUSEEVENTF_LEFTDOWN); break;
            case MacroFile.LeftUp: InputSender.Button(MOUSEEVENTF_LEFTUP); break;
            case MacroFile.RightDown: InputSender.Button(InputSender.MOUSEEVENTF_RIGHTDOWN); break;
            case MacroFile.RightUp: InputSender.Button(InputSender.MOUSEEVENTF_RIGHTUP); break;
            case MacroFile.MiddleDown: InputSender.Button(InputSender.MOUSEEVENTF_MIDDLEDOWN); break;
            case MacroFile.MiddleUp: InputSender.Button(InputSender.MOUSEEVENTF_MIDDLEUP); break;
            case MacroFile.Wheel: InputSender.Wheel(e[2]); break;
        }
    }

    /// <summary>Let go of everything the macro is holding, so no key is ever left stuck down.</summary>
    void ReleaseAll()
    {
        int[] held;
        lock (_held) { held = new int[_held.Count]; _held.CopyTo(held); _held.Clear(); }
        foreach (var code in held)
        {
            if (code == -1) InputSender.Button(MOUSEEVENTF_LEFTUP);
            else if (code == -2) InputSender.Button(InputSender.MOUSEEVENTF_RIGHTUP);
            else if (code == -3) InputSender.Button(InputSender.MOUSEEVENTF_MIDDLEUP);
            else if (code >= 0) InputSender.Key((ushort)code, 0, false, true);
        }
    }

    void SetState(MacroState s, int gen)
    {
        lock (_held) { if (gen == _generation && !(State == MacroState.Off)) State = s; }
    }

    static RECT? ParseRect(string s)
    {
        var p = s?.Split(',');
        if (p == null || p.Length != 4 || !int.TryParse(p[0], out var x) || !int.TryParse(p[1], out var y) ||
            !int.TryParse(p[2], out var w) || !int.TryParse(p[3], out var h) || w < 100 || h < 100) return null;
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    public void Dispose()
    {
        _quit = true;
        _wake.Set();
        _thread.Join(1000);
        ReleaseAll();
    }
}
