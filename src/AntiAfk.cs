using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static AfkHub.Native;

namespace AfkHub;

/// <summary>
/// Per-window anti-AFK. Instead of one shared timer that only presses in the newest window,
/// every Roblox window has its own idle timer and they're handled one at a time.
/// When a macro is playing in one window, anti-AFK asks it to pause at a safe point before switching
/// to the other windows, then hands the foreground back.
/// </summary>
sealed class AntiAfk
{
    public sealed record KeyOption(string Id, string Name, ushort Vk, bool Extended);

    public static readonly KeyOption[] Keys =
    {
        new("Backslash", "Backslash  \\  (recommended)", 0xDC, false),
        new("Space", "Space (jumps)", 0x20, false),
        new("RightCtrl", "Right Ctrl", 0xA3, true),
        new("Insert", "Insert", 0x2D, true),
    };

    static readonly TimeSpan PostponeLimit = TimeSpan.FromSeconds(90);
    /// <summary>How early to ask a running macro to wrap up its loop before an idle window is due.</summary>
    static readonly TimeSpan MacroLead = TimeSpan.FromSeconds(45);

    readonly Config _cfg;
    volatile bool _busy;
    uint _lastInputTick;
    bool _yieldOwned;

    public event Action<RobloxInstance> Pressed;
    public event Action<RobloxInstance, string> Failed;

    public AntiAfk(Config cfg) => _cfg = cfg;

    public MacroEngine Macro { get; set; }
    /// <summary>Milliseconds since a real person touched the PC (ignores the macro's own input), when hooks are running.</summary>
    public Func<int?> RealIdleMs { get; set; }
    /// <summary>The FPS a window is running at, so key presses are held long enough for it to notice.</summary>
    public Func<RobloxInstance, int> WindowFps { get; set; } = _ => 60;

    public bool Busy => _busy;

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, Math.Min(19, _cfg.AntiAfkMinutes)));

    public void Tick(IReadOnlyList<RobloxInstance> windows, bool enabled, DateTime now)
    {
        if (_busy) return;

        // Input while a Roblox window is in front counts as activity for that window only
        // (that includes a macro playing in it).
        uint tick = LastInputTick();
        if (tick != _lastInputTick)
        {
            IntPtr fg = GetForegroundWindow();
            foreach (var w in windows)
                if (w.Hwnd == fg) { w.LastActivityUtc = now; w.Postponed = false; }
            _lastInputTick = tick;
        }

        var macro = Macro;
        bool macroOn = macro is { Active: true };
        // A playing macro keeps its own window active, so anti-AFK only looks after the others.
        var macroWin = macroOn && macro.State is MacroState.Playing or MacroState.Yielding or MacroState.Yielded or MacroState.WaitingFocus
            ? macro.Window : null;

        if (!enabled) { ReleaseYield(); return; }

        var candidates = windows.Where(w => w.AntiAfkOn && !w.Disconnected && w != macroWin).ToList();
        var due = candidates
            .Where(w => w.ForceDue || now - w.LastActivityUtc >= Interval)
            .OrderByDescending(w => w.ForceDue).ThenBy(w => w.LastActivityUtc)
            .FirstOrDefault();

        if (macroOn)
        {
            bool dueSoon = candidates.Any(w => w.ForceDue || now - w.LastActivityUtc >= Interval - MacroLead);
            if (!dueSoon) { ReleaseYield(); return; }
            // Ask the macro to finish its loop and let go of the keys; wait until it has.
            macro.RequestYield();
            _yieldOwned = true;
            if (due == null || !macro.Yielded) return;
        }
        else _yieldOwned = false;

        if (due == null) return;

        bool inFront = due.Hwnd == GetForegroundWindow();
        if (!inFront && !due.ForceDue && _cfg.WaitWhileBusy)
        {
            // Don't yank focus while you're typing or moving the mouse elsewhere, up to a limit.
            bool overdue = now - due.LastActivityUtc >= Interval + PostponeLimit;
            int idle = RealIdleMs?.Invoke() ?? (int)IdleMilliseconds();
            if (idle < 2500 && !overdue) { due.Postponed = true; return; }
        }

        _busy = true;
        due.ForceDue = false;
        due.Postponed = false;
        Task.Run(() =>
        {
            try
            {
                if (Perform(due)) Pressed?.Invoke(due);
                else Failed?.Invoke(due, $"Anti-AFK couldn't bring {due.Label} to the front; will retry.");
            }
            catch (Exception ex) { Failed?.Invoke(due, $"Anti-AFK error on {due.Label}: {ex.Message}"); }
            finally
            {
                Thread.Sleep(1500); // stagger windows so actions never overlap
                _lastInputTick = LastInputTick();
                _busy = false;
            }
        });
    }

    void ReleaseYield()
    {
        if (!_yieldOwned) return;
        _yieldOwned = false;
        Macro?.EndYield();
    }

    bool Perform(RobloxInstance w)
    {
        IntPtr hwnd = w.Hwnd;
        if (!IsWindow(hwnd)) return false;
        var key = Keys.FirstOrDefault(k => k.Id == _cfg.AntiAfkKey) ?? Keys[0];

        IntPtr original = GetForegroundWindow();
        if (original == IntPtr.Zero)
            throw new InvalidOperationException("Windows looks locked or asleep, so keys can't be sent. Turn on \"Keep the screen on\" in Settings.");
        if (original == hwnd)
        {
            PressKey(key, w);
            Done(w);
            return true;
        }

        GetCursorPos(out var cursor);
        bool wasMinimized = IsIconic(hwnd);
        try
        {
            if (wasMinimized) { ShowWindow(hwnd, SW_RESTORE); Thread.Sleep(400); }

            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            // A real click is the most reliable way to give Roblox focus.
            GetWindowRect(hwnd, out var r);
            InputSender.MoveTo((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            Thread.Sleep(40);
            InputSender.Click();
            Thread.Sleep(120);
            if (GetForegroundWindow() != hwnd) { InputSender.AltUnlock(); SetForegroundWindow(hwnd); Thread.Sleep(150); }
            if (GetForegroundWindow() != hwnd) return false;

            Thread.Sleep(Math.Max(350, FrameMs(w) * 2));
            PressKey(key, w);
            Thread.Sleep(200);
            Done(w);
            return true;
        }
        finally
        {
            InputSender.MoveTo(cursor.X, cursor.Y);
            if (original != IntPtr.Zero && IsWindow(original))
            {
                for (int i = 0; i < 3 && GetForegroundWindow() != original; i++)
                {
                    InputSender.AltUnlock(); // Alt goes to the Roblox window, which ignores it
                    SetForegroundWindow(original);
                    Thread.Sleep(60);
                }
            }
            // Minimize without activating anything, so focus stays on your window.
            if (wasMinimized) ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
        }
    }

    static void Done(RobloxInstance w)
    {
        w.LastActivityUtc = DateTime.UtcNow;
        w.LastPressUtc = w.LastActivityUtc;
        w.Presses++;
    }

    int FrameMs(RobloxInstance w) => 1000 / Math.Max(1, WindowFps(w));

    void PressKey(KeyOption key, RobloxInstance w)
    {
        // Roblox reads input once per frame; hold for ~2.5 frames so a 5 FPS window doesn't miss it.
        int hold = Math.Max(40, Math.Min(600, FrameMs(w) * 5 / 2));
        InputSender.Tap(key.Vk, key.Extended, hold);
        // Backslash toggles Roblox's UI navigation, so a second press turns it back off.
        if (key.Id == "Backslash") { Thread.Sleep(hold); InputSender.Tap(key.Vk, key.Extended, hold); }
    }
}
