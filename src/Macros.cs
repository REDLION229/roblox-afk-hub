using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using static AfkHub.Native;

namespace AfkHub;

/// <summary>
/// A recorded input sequence. Each event is [timeMs, type, a, b]:
///   keys:   a = virtual key, b = scan code | (extended ? 0x10000 : 0)
///   mouse:  a, b = position. With Coords "window" they're 0–10000 across the Roblox window's game area,
///           so the macro still lands right if the window moves or resizes. "screen" = raw screen pixels
///           (TinyTask imports), which only line up if the window is where it was when recorded.
///   wheel:  a = delta
/// </summary>
public sealed class MacroFile
{
    public const int KeyDown = 0, KeyUp = 1, Move = 2, LeftDown = 3, LeftUp = 4, RightDown = 5, RightUp = 6,
        MiddleDown = 7, MiddleUp = 8, Wheel = 9;
    public const int Scale = 10000;

    public string Id { get; set; }
    public string Name { get; set; }
    public string Coords { get; set; } = "window";
    public string Source { get; set; } = "recorded";
    public int DurationMs { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>The game it was made for (0 = not tied to one), so it's offered first for windows in that game.</summary>
    public long PlaceId { get; set; }
    public string Game { get; set; }
    public List<int[]> Events { get; set; } = new();

    public static bool IsMouse(int type) => type >= Move && type <= MiddleUp;
    public static bool IsDown(int type) => type is KeyDown or LeftDown or RightDown or MiddleDown;
    public static bool IsUp(int type) => type is KeyUp or LeftUp or RightUp or MiddleUp;
}

/// <summary>Which macro (if any) runs in a window. Saved per account so it survives rejoins and restarts.</summary>
public sealed class MacroAssignment
{
    public string MacroId { get; set; }
    public double Speed { get; set; } = 1.0;
    public bool Loop { get; set; } = true;
    /// <summary>When several windows take turns: how many loops this window plays before handing over.</summary>
    public int LoopsPerTurn { get; set; } = 1;
    /// <summary>Window position/size to pin it to while the macro runs ("x,y,w,h").</summary>
    public string PinnedRect { get; set; }
}

sealed class MacroLibrary
{
    static readonly string Dir = Path.Combine(Store.Dir, "macros");
    readonly System.Web.Script.Serialization.JavaScriptSerializer _json = new() { MaxJsonLength = int.MaxValue };

    public List<MacroFile> All { get; } = new();

    public MacroLibrary()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                var m = _json.Deserialize<MacroFile>(File.ReadAllText(f));
                if (m?.Id != null && m.Events != null) All.Add(m);
            }
            catch { /* skip unreadable files */ }
        }
        All.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
    }

    public MacroFile Get(string id) => id == null ? null : All.FirstOrDefault(m => m.Id == id);

    public void Save(MacroFile m)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, m.Id + ".json"), _json.Serialize(m));
        if (!All.Contains(m)) All.Insert(0, m);
    }

    public void Delete(MacroFile m)
    {
        All.Remove(m);
        try { File.Delete(Path.Combine(Dir, m.Id + ".json")); } catch { }
    }

    public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

    /// <summary>
    /// TinyTask .rec files are a raw array of Windows journal EVENTMSG records (20 bytes each):
    /// message, paramL, paramH, time, hwnd. Keys: paramL = vk | scan &lt;&lt; 8, paramH bit 15 = extended.
    /// Mouse: paramL/paramH = screen x/y.
    /// </summary>
    public MacroFile ImportTinyTask(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || bytes.Length % 20 != 0) throw new InvalidDataException("That doesn't look like a TinyTask recording (.rec).");

        var events = new List<int[]>();
        uint t0 = BitConverter.ToUInt32(bytes, 12);
        for (int o = 0; o < bytes.Length; o += 20)
        {
            int msg = (int)BitConverter.ToUInt32(bytes, o);
            uint pl = BitConverter.ToUInt32(bytes, o + 4), ph = BitConverter.ToUInt32(bytes, o + 8);
            int t = (int)unchecked(BitConverter.ToUInt32(bytes, o + 12) - t0);
            if (t < 0 || t > 6 * 3600 * 1000) continue;
            int x = unchecked((int)pl), y = unchecked((int)ph);
            switch (msg)
            {
                case WM_KEYDOWN: case WM_SYSKEYDOWN:
                case WM_KEYUP: case WM_SYSKEYUP:
                    int vk = (int)(pl & 0xFF), scan = (int)((pl >> 8) & 0xFF);
                    bool ext = (ph & 0x8000) != 0;
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    events.Add(new[] { t, down ? MacroFile.KeyDown : MacroFile.KeyUp, vk, scan | (ext ? 0x10000 : 0) });
                    break;
                case WM_MOUSEMOVE: events.Add(new[] { t, MacroFile.Move, x, y }); break;
                case WM_LBUTTONDOWN: events.Add(new[] { t, MacroFile.LeftDown, x, y }); break;
                case WM_LBUTTONUP: events.Add(new[] { t, MacroFile.LeftUp, x, y }); break;
                case WM_RBUTTONDOWN: events.Add(new[] { t, MacroFile.RightDown, x, y }); break;
                case WM_RBUTTONUP: events.Add(new[] { t, MacroFile.RightUp, x, y }); break;
                case WM_MBUTTONDOWN: events.Add(new[] { t, MacroFile.MiddleDown, x, y }); break;
                case WM_MBUTTONUP: events.Add(new[] { t, MacroFile.MiddleUp, x, y }); break;
            }
        }
        // Drop F8 (usually the key that stopped TinyTask, and AFK Hub's own stop key), and release anything
        // the recording left held down at the end so a loop never ends with a stuck key.
        events.RemoveAll(e => (e[1] == MacroFile.KeyDown || e[1] == MacroFile.KeyUp) && e[2] == MacroRecorder.StopKey);
        if (events.Count == 0) throw new InvalidDataException("No keyboard or mouse actions were found in that recording.");
        int endT = events[events.Count - 1][0];
        var held = new Dictionary<int, int[]>();
        foreach (var e in events)
        {
            int code = e[1] <= MacroFile.KeyUp ? e[2] : -((e[1] + 1) / 2); // buttons: down (odd) + up (even) share a code
            if (MacroFile.IsDown(e[1])) held[code] = e;
            else if (MacroFile.IsUp(e[1])) held.Remove(code);
        }
        foreach (var d in held.Values)
            events.Add(d[1] == MacroFile.KeyDown ? new[] { endT, MacroFile.KeyUp, d[2], d[3] } : new[] { endT, d[1] + 1, d[2], d[3] });

        var m = new MacroFile
        {
            Id = NewId(),
            Name = Path.GetFileNameWithoutExtension(path),
            Coords = "screen",
            Source = "tinytask",
            CreatedUtc = DateTime.UtcNow,
            Events = events,
            DurationMs = events[events.Count - 1][0] + 250,
        };
        Save(m);
        return m;
    }

    /// <summary>Converts a fixed-screen-position macro so its clicks follow the given window from now on.</summary>
    public void FitToWindow(MacroFile m, IntPtr hwnd)
    {
        if (m.Coords == "window") return;
        if (!InputSender.ClientRect(hwnd, out var r)) throw new InvalidOperationException("Couldn't read that window's position.");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        foreach (var e in m.Events.Where(e => MacroFile.IsMouse(e[1])))
        {
            e[2] = Clamp((int)Math.Round((e[2] - r.Left) * (double)MacroFile.Scale / w));
            e[3] = Clamp((int)Math.Round((e[3] - r.Top) * (double)MacroFile.Scale / h));
        }
        m.Coords = "window";
        Save(m);
    }

    static int Clamp(int v) => Math.Max(0, Math.Min(MacroFile.Scale, v));
}

/// <summary>
/// Records real keyboard/mouse input that happens while the chosen Roblox window is in front.
/// Fed by <see cref="InputMonitor"/> on the hook thread, so every handler here is quick.
/// </summary>
sealed class MacroRecorder
{
    public const int StopKey = 0x77; // F8

    readonly object _lock = new();
    readonly List<int[]> _events = new();
    readonly HashSet<int> _heldKeys = new();
    readonly Stopwatch _clock = new();
    IntPtr _hwnd;
    int _lastMoveMs = -100, _lastX = -1, _lastY = -1;

    public bool Active { get; private set; }
    public RobloxInstance Window { get; private set; }
    public int EventCount { get { lock (_lock) return _events.Count; } }
    public int ElapsedMs => (int)_clock.ElapsedMilliseconds;

    public void Begin(RobloxInstance w)
    {
        lock (_lock)
        {
            _events.Clear();
            _heldKeys.Clear();
            _lastMoveMs = -100;
            _lastX = _lastY = -1;
            Window = w;
            _hwnd = w.Hwnd;
            _clock.Restart();
            Active = true;
        }
    }

    public void Cancel() { lock (_lock) { Active = false; _events.Clear(); } }

    public void OnKey(int vk, int scan, bool extended, bool down, bool injected)
    {
        if (!Active || injected || vk == StopKey || GetForegroundWindow() != _hwnd) return;
        lock (_lock)
        {
            if (down && !_heldKeys.Add(vk)) return; // skip auto-repeat while a key is held
            if (!down && !_heldKeys.Remove(vk)) return;
            _events.Add(new[] { ElapsedMs, down ? MacroFile.KeyDown : MacroFile.KeyUp, vk, scan | (extended ? 0x10000 : 0) });
        }
    }

    public void OnMouse(int message, int x, int y, int wheel, bool injected)
    {
        if (!Active || injected || GetForegroundWindow() != _hwnd) return;
        if (!InputSender.ClientRect(_hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int nx = (int)Math.Round((x - r.Left) * (double)MacroFile.Scale / w);
        int ny = (int)Math.Round((y - r.Top) * (double)MacroFile.Scale / h);
        bool inside = nx >= 0 && ny >= 0 && nx <= MacroFile.Scale && ny <= MacroFile.Scale;
        int t = ElapsedMs;
        int type = message switch
        {
            WM_MOUSEMOVE => MacroFile.Move,
            WM_LBUTTONDOWN => MacroFile.LeftDown, WM_LBUTTONUP => MacroFile.LeftUp,
            WM_RBUTTONDOWN => MacroFile.RightDown, WM_RBUTTONUP => MacroFile.RightUp,
            WM_MBUTTONDOWN => MacroFile.MiddleDown, WM_MBUTTONUP => MacroFile.MiddleUp,
            WM_MOUSEWHEEL => MacroFile.Wheel,
            _ => -1,
        };
        if (type < 0) return;
        lock (_lock)
        {
            if (type == MacroFile.Move)
            {
                // Moves are thinned out (~60 per second) and only kept inside the game area.
                if (!inside || t - _lastMoveMs < 16 || (nx == _lastX && ny == _lastY)) return;
                _lastMoveMs = t;
                _lastX = nx; _lastY = ny;
            }
            if (type == MacroFile.Wheel) { _events.Add(new[] { t, type, wheel, 0 }); return; }
            if (MacroFile.IsDown(type) && !inside) return;
            _events.Add(new[] { t, type, Math.Max(0, Math.Min(MacroFile.Scale, nx)), Math.Max(0, Math.Min(MacroFile.Scale, ny)) });
        }
    }

    /// <summary>Stops recording. Returns null if nothing usable was captured.</summary>
    public MacroFile Finish(string name)
    {
        lock (_lock)
        {
            Active = false;
            int end = ElapsedMs;
            // Anything still held when you pressed F8 gets released at the end of the loop.
            foreach (var vk in _heldKeys) _events.Add(new[] { end, MacroFile.KeyUp, vk, 0 });
            _heldKeys.Clear();
            if (!_events.Any(e => e[1] != MacroFile.Move)) return null;

            // Start the loop at the first action, keeping a short lead-in.
            int first = Math.Max(0, _events.Min(e => e[0]) - 100);
            foreach (var e in _events) e[0] -= first;
            return new MacroFile
            {
                Id = MacroLibrary.NewId(),
                Name = name,
                Coords = "window",
                Source = "recorded",
                CreatedUtc = DateTime.UtcNow,
                Events = _events.OrderBy(e => e[0]).ToList(),
                DurationMs = end - first,
            };
        }
    }
}
