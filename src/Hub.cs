using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace AfkHub;

enum Level { Info, Good, Warn, Bad }

sealed record LogEntry(DateTime Time, string Text, Level Level);

/// <summary>The engine: owns settings, accounts, stats, and ticks multi-instance, anti-AFK and log watching once per second.</summary>
sealed class Hub : IDisposable
{
    public Config Cfg { get; }
    public AccountStore Accounts { get; }
    public StatsFile Stats { get; }
    public InstanceTracker Tracker { get; } = new();
    public AntiAfk AntiAfk { get; }
    public MacroLibrary Macros { get; } = new();
    public MacroEngine Engine { get; } = new();
    public MacroRecorder Recorder { get; } = new();
    public InputMonitor Monitor { get; } = new();
    public Notifier Notifier { get; }
    public Automation Automation { get; }
    public Watchdog Watchdog { get; }
    public LiveStatus LiveStatus { get; }
    /// <summary>null, "countdown" or "recording".</summary>
    public string RecordingStage { get; private set; }
    public event Action<MacroFile> RecordingFinished;

    public bool AfkMode { get; private set; }
    public bool Launching { get; private set; }
    public bool MultiBlocked { get; private set; }
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public event Action<string, Level> Logged;
    public event Action<RobloxInstance> Disconnected;
    /// <summary>A watched account fell out of the game (account name, what happened).</summary>
    public event Action<string, string> AccountDropped;
    public void RaiseAccountDropped(string account, string problem) => AccountDropped?.Invoke(account, problem);

    readonly SingletonBreaker _breaker = new();
    readonly SynchronizationContext _ui;
    bool _scanBusy, _restoreFpsPending;
    DateTime _lastScan, _lastLogs, _lastStatsSave, _noWindowsSince, _lastRefresh, _lastTrayCheck;
    DateTime _macroGraceUntil;
    bool _pauseLogged;
    uint _awakeState;

    public Hub()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Cfg = Store.Load<Config>("config.json");
        Accounts = Store.Load<AccountStore>("accounts.json");
        Stats = Store.Load<StatsFile>("stats.json");

        if (Stats.Current != null)
        {
            // The app was closed without ending the session (crash, shutdown); keep what was recorded.
            KeepSession(Stats.Current);
            Stats.Current = null;
            SaveStats();
        }
        if (Cfg.NormalFps <= 0)
        {
            Cfg.NormalFps = SafeReadFps() is int f && f > 0 && f != Cfg.AfkFps ? f : 60;
            SaveConfig();
        }

        AntiAfk = new AntiAfk(Cfg)
        {
            Macro = Engine,
            RealIdleMs = () => Monitor.Running ? Monitor.RealIdleMs : null,
            WindowFps = WindowFps,
        };

        // Macro assignments for unnamed windows only make sense for this run; and a macro never
        // starts by itself when the app opens — you press Start.
        foreach (var k in Cfg.MacroAssignments.Keys.Where(k => k.StartsWith("pid:")).ToList()) Cfg.MacroAssignments.Remove(k);
        Cfg.MacroRunKeys.Clear();

        Engine.Log += (msg, lvl) => _ui.Post(_ => Log(msg, lvl), null);
        Monitor.Key += (vk, scan, ext, down, injected) =>
        {
            Recorder.OnKey(vk, scan, ext, down, injected);
            if (down && !injected && vk == MacroRecorder.StopKey) _ui.Post(_ => OnStopKey(), null);
        };
        Monitor.Mouse += Recorder.OnMouse;
        Monitor.RealInput += () =>
        {
            if (Cfg.MacroPauseOnInput && DateTime.UtcNow > _macroGraceUntil) Engine.PauseForUser();
        };
        AntiAfk.Pressed += inst => _ui.Post(_ => OnPressed(inst), null);
        AntiAfk.Failed += (inst, msg) => _ui.Post(_ => Log(msg, Level.Warn), null);
        Tracker.Disconnected += OnDisconnected;
        Tracker.Log += msg => Log(msg);

        Notifier = new Notifier(Cfg);
        Notifier.Problem += msg => _ui.Post(_ => Log(msg, Level.Warn), null);
        Automation = new Automation(this);
        Watchdog = new Watchdog(this);
        LiveStatus = new LiveStatus(this);
    }

    /// <summary>Recent activity, so the UI can show history after it reloads (e.g. coming back from the tray).</summary>
    public List<LogEntry> RecentLog { get; } = new();

    public void Log(string message, Level level = Level.Info)
    {
        RecentLog.Add(new LogEntry(DateTime.Now, message, level));
        if (RecentLog.Count > 300) RecentLog.RemoveAt(0);
        Logged?.Invoke(message, level);
        WriteActivityLog(message, level);
    }

    /// <summary>The activity log also goes to activity.log in the data folder, so an overnight run can be checked in the morning.</summary>
    static void WriteActivityLog(string message, Level level)
    {
        try
        {
            var path = System.IO.Path.Combine(Store.Dir, "activity.log");
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > (2 << 20))
            {
                var old = System.IO.Path.Combine(Store.Dir, "activity.old.log");
                System.IO.File.Delete(old);
                System.IO.File.Move(path, old);
            }
            System.IO.Directory.CreateDirectory(Store.Dir);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {(level == Level.Info ? "" : level.ToString().ToUpperInvariant() + "  ")}{message}\r\n");
        }
        catch { }
    }

    public void SaveConfig() => Store.Save("config.json", Cfg);
    public void SaveAccounts() => Store.Save("accounts.json", Accounts);
    void SaveStats() { _lastStatsSave = DateTime.UtcNow; Store.Save("stats.json", Stats); }

    public static int? SafeReadFps()
    {
        try { return FpsFile.Read(); } catch { return null; }
    }

    // ---------- main loop ----------

    public void Tick()
    {
        var now = DateTime.UtcNow;

        // With no Roblox running there's nothing to react to quickly, so look less often.
        bool quiet = Tracker.Alive.Count == 0 && !Launching;
        if (!quiet || now - _lastRefresh >= TimeSpan.FromSeconds(3))
        {
            _lastRefresh = now;
            Tracker.Refresh(now);
        }

        if (Cfg.MultiInstance && !_scanBusy && Tracker.Alive.Count > 0)
        {
            // Fast while a new window is waiting to be unlocked, then just an occasional re-check.
            var wait = Tracker.Alive.Any(i => !i.EventCleared) ? 1.5 : 30;
            if ((now - _lastScan).TotalSeconds >= wait) _ = ScanAsync();
        }

        AntiAfk.Tick(Tracker.Windows, Cfg.AntiAfk && RecordingStage == null, now);
        foreach (var w in Tracker.Windows)
            if (w.PlaceId > 0 && w.PlaceId != w.RecordedPlace) { w.RecordedPlace = w.PlaceId; RecordRecent(w.PlaceId); }
        TickMacro(now);
        Automation.Tick(now);
        // Remember which windows run macros, so a restart can bring them back.
        if (AfkMode && !Cfg.ResumeMacroKeys.SequenceEqual(Cfg.MacroRunKeys)) { Cfg.ResumeMacroKeys = Cfg.MacroRunKeys.ToList(); SaveConfig(); }

        if (Tracker.Alive.Count > 0 && now - _lastLogs >= TimeSpan.FromSeconds(3))
        {
            _lastLogs = now;
            Tracker.UpdateLogs(now);
        }

        Watchdog.Tick(now);
        LiveStatus.Tick(now);

        if (!Launching && Tracker.Alive.Count > 0 && now - _lastTrayCheck >= TimeSpan.FromSeconds(10))
        {
            _lastTrayCheck = now;
            CloseLeftoverTrayProcesses(now);
        }

        UpdateKeepAwake();

        // Roblox writes its in-memory settings back to the file when a window closes, so once the
        // windows that were launched with special FPS/graphics are gone, put the intended values back.
        if (_restoreFpsPending)
        {
            if (Tracker.Windows.Count > 0) _noWindowsSince = DateTime.MaxValue;
            else if (_noWindowsSince == DateTime.MaxValue) _noWindowsSince = now;
            else if (now - _noWindowsSince > TimeSpan.FromSeconds(5))
            {
                _restoreFpsPending = false;
                int target = AfkMode ? Cfg.AfkFps : Cfg.NormalFps;
                bool graphics = Cfg.GraphicsBaseline != null;
                if (RestoreSettings(target))
                    Log($"Roblox windows closed — settings back to {target} FPS{(graphics ? " and your normal graphics" : "")}.", Level.Good);
                Cfg.GraphicsBaseline = null;
                SaveConfig();
            }
        }

        if (Stats.Current is { } s)
        {
            // Time each account actually spent in game, so a window that died early shows up in the stats.
            double step = Math.Min(5, (now - (s.EndUtc > s.StartUtc ? s.EndUtc : now)).TotalSeconds);
            s.EndUtc = now;
            s.MaxWindows = Math.Max(s.MaxWindows, Tracker.Windows.Count);
            foreach (var w in Tracker.Windows)
            {
                AddSessionAccount(w);
                if (step > 0 && (w.AccountRef != null || w.CustomName != null) && Watchdog.InGame(w)) AddInGame(s, w.Label, step);
            }
            if (now - _lastStatsSave > TimeSpan.FromMinutes(1)) SaveStats();
        }
    }

    // ---------- multi-instance ----------

    public async Task ScanAsync()
    {
        while (_scanBusy) await Task.Delay(100);
        _scanBusy = true;
        _lastScan = DateTime.UtcNow;
        try
        {
            var pids = Tracker.Alive.Select(i => i.Pid).ToList();
            if (pids.Count == 0) return;
            var results = await Task.Run(() => _breaker.Scan(pids));
            foreach (var kv in results)
            {
                var inst = Tracker.Get(kv.Key);
                if (inst == null) continue;
                switch (kv.Value)
                {
                    case ScanResult.Closed:
                        if (!inst.EventCleared) Log($"Multi-instance unlocked for {inst.Label}.", Level.Good);
                        inst.EventCleared = true;
                        inst.MultiState = "Unlocked";
                        break;
                    case ScanResult.AccessDenied:
                        inst.MultiState = IsAdmin ? "Blocked by Roblox" : "Blocked — restart as admin";
                        if (!MultiBlocked)
                            Log(IsAdmin
                                ? $"Roblox blocked access to {inst.Label}; multi-instance can't unlock it."
                                : $"Windows blocked access to {inst.Label}. Use Settings → Advanced → Restart as admin.", Level.Bad);
                        MultiBlocked = true;
                        break;
                    default:
                        // No lock in a process that's been running a while means nothing is blocking a new window
                        // (e.g. it was already unlocked earlier). Keep rescanning in case Roblox recreates it.
                        inst.EmptyScans++;
                        if (!inst.EventCleared && inst.EmptyScans >= 2 && DateTime.UtcNow - inst.StartedUtc > TimeSpan.FromSeconds(10))
                        {
                            inst.EventCleared = true;
                            inst.MultiState = "Unlocked";
                        }
                        else if (!inst.EventCleared) inst.MultiState = "Waiting…";
                        break;
                }
            }
        }
        catch (Exception ex) { Log("Multi-instance check failed: " + ex.Message, Level.Warn); }
        finally { _scanBusy = false; }
    }

    // ---------- macros ----------
    //
    // Any number of windows can run a macro. Roblox only reads input from the window in front, so they
    // take turns: the current window plays its loops for this turn, lets go of every key, and the next
    // window in Cfg.MacroRunKeys takes over. With one window it simply loops.

    public int WindowFps(RobloxInstance w) => w.LaunchFps > 0 ? w.LaunchFps : SafeReadFps() is int f && f > 0 ? f : 60;

    public MacroAssignment AssignmentFor(RobloxInstance w) =>
        Cfg.MacroAssignments.TryGetValue(w.MacroKey, out var a) ? a : null;

    public bool IsRunning(RobloxInstance w) => Cfg.MacroRunKeys.Contains(w.MacroKey);

    /// <summary>Total loops played across all turns since Start, per window.</summary>
    public Dictionary<string, int> LoopTotals { get; } = new();

    public void AssignMacro(RobloxInstance w, string macroId, double speed, bool loop, int loopsPerTurn)
    {
        var key = w.MacroKey;
        if (string.IsNullOrEmpty(macroId) || Macros.Get(macroId) == null)
        {
            if (IsRunning(w)) StopMacro(w, $"{w.Label} set back to idle.");
            Cfg.MacroAssignments.Remove(key);
        }
        else
        {
            if (!Cfg.MacroAssignments.TryGetValue(key, out var a)) Cfg.MacroAssignments[key] = a = new MacroAssignment();
            bool restart = a.MacroId != macroId || Math.Abs(a.Speed - speed) > 0.001 || a.Loop != loop;
            a.MacroId = macroId;
            a.Speed = speed;
            a.Loop = loop;
            a.LoopsPerTurn = Math.Max(1, Math.Min(100, loopsPerTurn));
            // Changing the macro/speed of the window that's playing right now takes effect immediately.
            if (restart && Engine.Window == w && IsRunning(w)) BeginTurn(w, a, Macros.Get(macroId), manual: false);
        }
        SaveConfig();
    }

    /// <summary>Adds a window to the turn order. If nothing is playing, it starts right away.</summary>
    public void StartMacro(RobloxInstance w)
    {
        var a = AssignmentFor(w);
        var m = Macros.Get(a?.MacroId);
        if (m == null) { Log($"Pick a macro for {w.Label} first.", Level.Warn); return; }
        if (RecordingStage != null) { Log("Finish the recording first (press F8).", Level.Warn); return; }
        a.PinnedRect = null; // pin it wherever you've put it now
        if (!IsRunning(w)) Cfg.MacroRunKeys.Add(w.MacroKey);
        LoopTotals[w.MacroKey] = 0;
        SaveConfig();
        Monitor.Start();
        _pauseLogged = false;

        if (Engine.Window == null || !Engine.Active || Engine.State is MacroState.WaitingWindow)
            BeginTurn(w, a, m, manual: true);
        else
            Log($"{w.Label} joined the macro rotation with \"{m.Name}\" — it plays when it's its turn.", Level.Good);

        if (WindowFps(w) < 20)
            Log($"{w.Label} is at {WindowFps(w)} FPS — macros can miss inputs that low. 30 FPS is a good minimum (Accounts → Opens at).", Level.Warn);
    }

    void BeginTurn(RobloxInstance w, MacroAssignment a, MacroFile m, bool manual)
    {
        if (manual) _macroGraceUntil = DateTime.UtcNow.AddSeconds(5); // time to take your hand off the mouse
        Engine.Start(w, m, a, Cfg.MacroPinWindows, () => WindowFps(w), TurnLength(a));
        if (manual)
            Log($"Macro \"{m.Name}\" playing in {w.Label}{(a.Speed != 1 ? $" at {a.Speed:0.##}×" : "")}. Press F8 to stop.", Level.Good);
    }

    /// <summary>0 = keep looping (it's the only window); otherwise loops before handing over.</summary>
    int TurnLength(MacroAssignment a) => Cfg.MacroRunKeys.Count > 1 ? Math.Max(1, a.LoopsPerTurn) : 0;

    /// <summary>Stops one window's macro, or all of them when <paramref name="w"/> is null.</summary>
    public void StopMacro(RobloxInstance w = null, string reason = null)
    {
        if (w == null)
        {
            if (Cfg.MacroRunKeys.Count == 0 && !Engine.Active) return;
            Cfg.MacroRunKeys.Clear();
            Engine.Stop();
            Log(reason ?? "Macros stopped.");
        }
        else
        {
            if (!Cfg.MacroRunKeys.Remove(w.MacroKey)) return;
            if (Engine.Window == w) Engine.Stop(); // the next window's turn starts on the next tick
            Log(reason ?? $"Macro stopped in {w.Label}.");
        }
        SaveConfig();
    }

    public void ResumeMacro()
    {
        _macroGraceUntil = DateTime.UtcNow.AddSeconds(5);
        _pauseLogged = false;
        Engine.ResumeFromUser();
        Log("Macro resumed.", Level.Good);
    }

    void OnStopKey()
    {
        if (RecordingStage == "recording") FinishRecording();
        else if (Engine.Active || Cfg.MacroRunKeys.Count > 0) StopMacro(null, "Macros stopped (F8).");
    }

    /// <summary>A rotation window is ready for a turn once it's been in game for a few seconds.</summary>
    RobloxInstance ReadyWindow(string key, DateTime now)
    {
        var w = Tracker.Windows.FirstOrDefault(x => x.MacroKey == key);
        if (w == null || w.Disconnected) return null;
        bool ready = w.JoinedUtc is { } j ? now - j > TimeSpan.FromSeconds(8) : now - w.StartedUtc > TimeSpan.FromSeconds(45);
        return ready ? w : null;
    }

    /// <summary>The window after <paramref name="afterKey"/> in the rotation that's ready to play (wraps around).</summary>
    public RobloxInstance NextInRotation(string afterKey, DateTime now)
    {
        var keys = Cfg.MacroRunKeys;
        if (keys.Count == 0) return null;
        int start = afterKey == null ? -1 : keys.IndexOf(afterKey);
        for (int i = 1; i <= keys.Count; i++)
        {
            var w = ReadyWindow(keys[(start + i + keys.Count) % keys.Count], now);
            if (w != null) return w;
        }
        return null;
    }

    void TickMacro(DateTime now)
    {
        // The input hooks only run while something needs them.
        bool needHooks = Engine.Active || RecordingStage != null || Cfg.MacroRunKeys.Count > 0;
        if (needHooks && !Monitor.Running) Monitor.Start();
        else if (!needHooks && Monitor.Running) Monitor.Stop();

        // Drop windows whose macro was deleted.
        foreach (var key in Cfg.MacroRunKeys.ToList())
            if (!Cfg.MacroAssignments.TryGetValue(key, out var ka) || Macros.Get(ka.MacroId) == null)
            {
                Cfg.MacroRunKeys.Remove(key);
                if (Engine.Window?.MacroKey == key) Engine.Stop();
            }

        if (Cfg.MacroRunKeys.Count == 0)
        {
            if (Engine.Active) Engine.Stop();
            return;
        }

        var cur = Engine.Window;
        string curKey = cur?.MacroKey;

        // Turn bookkeeping: a finished turn (or a single play with looping off) hands over to the next window.
        if (cur != null && Engine.State is MacroState.TurnDone or MacroState.Finished)
        {
            LoopTotals[curKey] = (LoopTotals.TryGetValue(curKey, out var t) ? t : 0) + Engine.Loops;
            if (Engine.State == MacroState.Finished)
            {
                Cfg.MacroRunKeys.Remove(curKey);
                SaveConfig();
                Log($"{cur.Label} played its macro once (looping is off) and left the rotation.");
            }
            StartNextTurn(curKey, now);
            return;
        }

        // The playing window went away (closed / disconnected / rejoining): move on, it rejoins the rotation when back.
        if (cur != null && (cur.Exited || cur.Disconnected || !Tracker.Windows.Contains(cur) || !Cfg.MacroRunKeys.Contains(curKey)))
        {
            if (Cfg.MacroRunKeys.Contains(curKey))
                Log($"{cur.Label} {(cur.Disconnected ? "disconnected" : "closed")}; its macro continues once it's back in game.", Level.Warn);
            Engine.Detach();
            StartNextTurn(curKey, now);
            return;
        }

        // Nothing playing (e.g. every window was away): start whoever is ready.
        if (cur == null || Engine.State is MacroState.Off or MacroState.WaitingWindow)
        {
            StartNextTurn(curKey, now);
            return;
        }

        // Windows joining or leaving changes whether the current one loops forever or takes turns.
        if (AssignmentFor(cur) is { } ca) Engine.SetTurnLength(TurnLength(ca));

        if (Engine.PausedForUser)
        {
            if (!_pauseLogged) { _pauseLogged = true; Log("Macro paused — you used the PC. Anti-AFK is still covering every window.", Level.Warn); }
            if (Cfg.MacroAutoResume && Monitor.RealIdleMs >= Math.Max(5, Cfg.MacroResumeAfterSec) * 1000) ResumeMacro();
        }
    }

    void StartNextTurn(string afterKey, DateTime now)
    {
        var next = NextInRotation(afterKey, now);
        if (next == null) { if (Engine.Window != null) Engine.Detach(); return; }
        var a = AssignmentFor(next);
        var m = Macros.Get(a?.MacroId);
        if (m == null) return;
        BeginTurn(next, a, m, manual: false);
    }

    public async Task StartRecordingAsync(RobloxInstance w)
    {
        if (RecordingStage != null) return;
        if (Engine.Active || Cfg.MacroRunKeys.Count > 0) StopMacro(null, "Macros stopped so you can record.");
        RecordingStage = "countdown";
        Monitor.Start();
        Log($"Recording in {w.Label} starts in 3 seconds. Press F8 when you're done.");
        await Task.Run(() => InputSender.BringToFront(w.Hwnd));
        for (int i = 0; i < 3; i++) { System.Media.SystemSounds.Beep.Play(); await Task.Delay(1000); }
        if (RecordingStage != "countdown") return;
        if (!Native.IsWindow(w.Hwnd)) { RecordingStage = null; Log("That window closed, so recording was cancelled.", Level.Warn); return; }
        Recorder.Begin(w);
        RecordingStage = "recording";
        System.Media.SystemSounds.Asterisk.Play();
        Log("Recording… press F8 to stop.", Level.Good);

        // Safety net: stop by itself after 30 minutes.
        await Task.Delay(TimeSpan.FromMinutes(30));
        if (RecordingStage == "recording" && Recorder.Window == w) FinishRecording();
    }

    public MacroFile FinishRecording()
    {
        if (RecordingStage == null) return null;
        bool wasRecording = RecordingStage == "recording";
        RecordingStage = null;
        MacroFile m = null;
        if (wasRecording)
        {
            long place = Recorder.Window?.PlaceId ?? 0;
            m = Recorder.Finish(MacroNameFor(place));
            if (m != null)
            {
                TieToGame(m, place);
                Macros.Save(m);
                Log($"Recorded \"{m.Name}\" — {m.Events.Count(e => e[1] != MacroFile.Move)} actions, {m.DurationMs / 1000.0:0.#} s.", Level.Good);
            }
            else Log("Nothing was recorded (no keys or clicks in that window).", Level.Warn);
        }
        else Recorder.Cancel();
        System.Media.SystemSounds.Asterisk.Play();
        RecordingFinished?.Invoke(m);
        return m;
    }

    /// <summary>The game's name for a place your windows have joined (null if unknown).</summary>
    public string GameName(long placeId) => placeId > 0 ? Cfg.RecentPlaces.FirstOrDefault(r => r.PlaceId == placeId)?.Game : null;

    /// <summary>"[Game] Macro" (or "[Game] Macro 2"…) for the game a window is in.</summary>
    public string MacroNameFor(long placeId)
    {
        var game = GameName(placeId);
        if (game == null) return $"Macro {DateTime.Now:MMM d h.mm tt}";
        string name = $"{game} Macro";
        for (int n = 2; Macros.All.Any(x => x.Name == name); n++) name = $"{game} Macro {n}";
        return name;
    }

    public void TieToGame(MacroFile m, long placeId)
    {
        m.PlaceId = placeId;
        m.Game = GameName(placeId);
    }

    /// <summary>The one game every in-game window is playing, or 0 when there's none or several.</summary>
    public long CurrentPlace()
    {
        var places = Tracker.Windows.Where(w => w.PlaceId > 0).Select(w => w.PlaceId).Distinct().ToList();
        return places.Count == 1 ? places[0] : 0;
    }

    void RecordRecent(long placeId)
    {
        var r = Cfg.RecentPlaces.FirstOrDefault(x => x.PlaceId == placeId);
        if (r == null) Cfg.RecentPlaces.Insert(0, r = new RecentPlace { PlaceId = placeId });
        else { Cfg.RecentPlaces.Remove(r); Cfg.RecentPlaces.Insert(0, r); }
        r.LastUtc = DateTime.UtcNow;
        r.Joins++;
        if (Cfg.RecentPlaces.Count > 12) Cfg.RecentPlaces.RemoveRange(12, Cfg.RecentPlaces.Count - 12);
        SaveConfig();
    }

    void CloseLeftoverTrayProcesses(DateTime now)
    {
        foreach (var inst in Tracker.LeftoverTrayProcesses(now))
        {
            try
            {
                using var p = Process.GetProcessById(inst.Pid);
                p.Kill();
                Log($"Closed a leftover background Roblox (PID {inst.Pid}) that was sitting in the tray.", inst.HadWindow ? Level.Warn : Level.Info);
            }
            catch (Exception ex) { Log($"Couldn't close background Roblox (PID {inst.Pid}): {ex.Message}", Level.Warn); }
        }
    }

    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    /// <summary>Stops Windows sleeping (and optionally turning off/locking the screen) while Roblox windows are open.</summary>
    void UpdateKeepAwake()
    {
        uint want = ES_CONTINUOUS;
        if (Cfg.KeepAwake && (AfkMode || Tracker.Windows.Count > 0))
            want |= ES_SYSTEM_REQUIRED | (Cfg.KeepScreenOn ? ES_DISPLAY_REQUIRED : 0);
        if (want == _awakeState) return;
        Native.SetThreadExecutionState(want);
        if ((want & ES_SYSTEM_REQUIRED) != 0 && (_awakeState & ES_SYSTEM_REQUIRED) == 0)
            Log(Cfg.KeepScreenOn ? "Keeping the PC and screen awake while Roblox is open." : "Keeping the PC awake while Roblox is open.");
        _awakeState = want;
    }

    public static void RestartAsAdmin()
    {
        Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, "--elevated") { UseShellExecute = true, Verb = "runas" });
    }

    // ---------- FPS / AFK mode ----------

    public bool TrySetFps(int fps, bool quiet = false)
    {
        try
        {
            FpsFile.Write(fps);
            if (!quiet)
                Log(Tracker.Windows.Count > 0
                    ? $"FPS cap set to {fps}. Already-open Roblox windows keep their FPS until you rejoin."
                    : $"FPS cap set to {fps}.", Level.Good);
            return true;
        }
        catch (Exception ex) { Log("Couldn't change FPS: " + ex.Message, Level.Bad); return false; }
    }

    public void StartAfk()
    {
        if (AfkMode) return;
        AfkMode = true;
        _restoreFpsPending = false;
        // Locked so Roblox can't overwrite the FPS when a window closes.
        if (TrySetFps(Cfg.AfkFps, quiet: true))
        {
            try { FpsFile.SetLocked(true); } catch (Exception ex) { Log("Couldn't lock settings file: " + ex.Message, Level.Warn); }
        }
        Cfg.AntiAfk = true;
        Cfg.ResumePending = true;
        SaveConfig();
        var now = DateTime.UtcNow;
        Stats.Current = new SessionRecord { StartUtc = now, EndUtc = now };
        SaveStats();
        // Accounts already open from the app are kept in game from now on too.
        foreach (var w in Tracker.Alive.Where(w => !w.Exited && w.AccountRef != null && w.Target != null)) Watchdog.Watch(w.AccountRef, w.Target);
        Log($"AFK mode on — FPS {Cfg.AfkFps} (locked), anti-AFK on.", Level.Good);
    }

    /// <param name="byUser">false when Windows is shutting down: the session is kept so it can resume after the restart.</param>
    public void StopAfk(bool byUser = true)
    {
        if (!AfkMode) return;
        AfkMode = false;
        if (byUser) { Cfg.ResumePending = false; Cfg.ResumeMacroKeys.Clear(); SaveConfig(); }
        try { FpsFile.SetLocked(false); } catch { }
        TrySetFps(Cfg.NormalFps, quiet: true);
        if (Tracker.Windows.Count > 0) { _restoreFpsPending = true; _noWindowsSince = DateTime.MaxValue; }

        // A restart keeps the live status as it is, since the session resumes afterwards.
        if (byUser) LiveStatus.Finish();
        if (Stats.Current is { } s)
        {
            s.EndUtc = DateTime.UtcNow;
            KeepSession(s);
            _inGameCarry.Clear();
            Stats.Current = null;
            SaveStats();
            Log($"AFK mode off after {Format.Duration(s.Duration)} — FPS back to {Cfg.NormalFps}.", Level.Good);
            if (byUser)
            {
                // Say how each account actually finished, not just which ones were launched.
                var watched = Watchdog.Accounts;
                Notifier.Summary("AFK session finished",
                    $"**{Format.Duration(s.Duration)}** AFK\n" +
                    $"{s.Presses} anti-AFK press{(s.Presses == 1 ? "" : "es")} · {s.Disconnects} disconnect{(s.Disconnects == 1 ? "" : "s")} · {s.Rejoins} rejoin{(s.Rejoins == 1 ? "" : "s")}" +
                    (watched.Count > 0
                        ? $"\n{watched.Count(w => w.State == "ok")}/{watched.Count} accounts in game at the end:\n{Watchdog.StatusLines()}"
                        : s.Accounts.Count > 0 ? $"\nAccounts: {string.Join(", ", s.Accounts)}" : ""));
            }
        }
        Watchdog.Clear();
    }

    /// <summary>
    /// Lays the Roblox windows out on the screen the first one is on: side by side in a grid ("tile"),
    /// or neatly overlapping ("stack"). Minimized windows are restored first. Macros keep working:
    /// their pinned spot moves with the window, and their clicks are relative to the window anyway.
    /// </summary>
    public void ArrangeWindows(bool tile)
    {
        var wins = Tracker.Windows.ToList();
        if (wins.Count == 0) return;
        var area = System.Windows.Forms.Screen.FromHandle(wins[0].Hwnd).WorkingArea;
        int n = wins.Count;
        int cols = tile ? (n <= 1 ? 1 : n <= 4 ? 2 : 3) : 1, rows = tile ? (n + cols - 1) / cols : 1;
        int stackW = (int)(area.Width * 0.6), stackH = (int)(area.Height * 0.65), step = 36;

        for (int i = 0; i < n; i++)
        {
            var w = wins[i];
            if (Native.IsIconic(w.Hwnd)) Native.ShowWindow(w.Hwnd, Native.SW_RESTORE);
            Native.RECT r;
            if (tile)
            {
                int cw = area.Width / cols, ch = area.Height / rows;
                r = new Native.RECT { Left = area.Left + (i % cols) * cw, Top = area.Top + (i / cols) * ch };
                r.Right = r.Left + cw; r.Bottom = r.Top + ch;
            }
            else
            {
                int off = Math.Min(i * step, Math.Max(0, Math.Min(area.Width - stackW, area.Height - stackH)));
                r = new Native.RECT { Left = area.Left + 40 + off, Top = area.Top + 30 + off };
                r.Right = r.Left + stackW; r.Bottom = r.Top + stackH;
            }
            Engine.MovePin(w, r); // first, so a running macro doesn't snap it back
            foreach (var a in Cfg.MacroAssignments.Where(kv => kv.Key == w.MacroKey && kv.Value.PinnedRect != null).Select(kv => kv.Value))
                a.PinnedRect = $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}";
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        SaveConfig();
        Log(tile ? $"Tiled {n} Roblox window{(n == 1 ? "" : "s")} side by side." : $"Stacked {n} Roblox window{(n == 1 ? "" : "s")}.", Level.Good);
    }

    /// <summary>"I'm back" plus closing every Roblox window, for when you don't need to look at them.</summary>
    bool _cancelLaunch;

    public void BackAndCloseAll()
    {
        if (Launching) _cancelLaunch = true; // don't keep opening accounts after you asked to close everything
        StopMacro(null, "Macros stopped.");
        StopAfk(); // first, so the watchdog stops expecting these accounts and doesn't rejoin them
        int n = 0;
        foreach (var w in Tracker.Alive.ToList())
            try { using var p = Process.GetProcessById(w.Pid); p.Kill(); n++; } catch { }
        if (n > 0) Log($"Closed {n} Roblox window{(n == 1 ? "" : "s")}.", Level.Good);
    }

    void AddSessionAccount(RobloxInstance inst)
    {
        // Only windows with a real name (launched account or one you named) are worth listing.
        if (inst.AccountRef == null && inst.CustomName == null) return;
        if (Stats.Current is { } s && !s.Accounts.Contains(inst.Label)) s.Accounts.Add(inst.Label);
    }

    readonly Dictionary<string, double> _inGameCarry = new();

    void AddInGame(SessionRecord s, string label, double seconds)
    {
        double total = (_inGameCarry.TryGetValue(label, out var c) ? c : 0) + seconds;
        int whole = (int)total;
        _inGameCarry[label] = total - whole;
        if (whole > 0) s.InGameSeconds[label] = (s.InGameSeconds.TryGetValue(label, out var v) ? v : 0) + whole;
    }

    /// <summary>Sessions under a minute are just Start/Stop clicks; they're not worth keeping.</summary>
    void KeepSession(SessionRecord s)
    {
        if (s.Duration < TimeSpan.FromMinutes(1)) return;
        Stats.Sessions.Insert(0, s);
        if (Stats.Sessions.Count > 300) Stats.Sessions.RemoveRange(300, Stats.Sessions.Count - 300);
    }

    void OnPressed(RobloxInstance inst)
    {
        if (Stats.Current is { } s) s.Presses++;
        Log($"Anti-AFK kept {inst.Label} active.");
    }

    // ---------- disconnects ----------

    void OnDisconnected(RobloxInstance inst)
    {
        // AFK mode accounts: the watchdog counts it, alerts and rejoins (with retries), so nothing is done twice.
        if (AfkMode && Watchdog.IsWatching(inst))
        {
            Log($"{inst.Label}: {inst.Status}.", Level.Bad);
            Watchdog.Nudge();
            return;
        }
        if (Stats.Current is { } s) s.Disconnects++;
        Log($"{inst.Label}: {inst.Status}.", Level.Bad);
        Disconnected?.Invoke(inst);
        bool willRejoin = Cfg.AutoRejoin && inst.AccountRef != null && inst.Target != null && inst.Rejoins < 3;
        Notifier.Alert("Roblox disconnected", $"**{inst.Label}** — {inst.Status}." + (willRejoin ? " Rejoining automatically." : ""));

        if (Cfg.AutoRejoin && inst.AccountRef != null && inst.Target != null)
        {
            if (inst.Rejoins >= 3)
            {
                Log($"Not rejoining {inst.Label} again — it already failed 3 times.", Level.Warn);
                Notifier.Alert("Gave up rejoining", $"**{inst.Label}** failed to stay connected 3 times. It needs a look.");
            }
            else _ = RejoinAsync(inst);
        }
    }

    /// <summary>Closes a window and launches its account back into the same game (frozen-window recovery).</summary>
    public void Rejoin(RobloxInstance inst, string reason)
    {
        if (inst.AccountRef == null || inst.Target == null) return;
        if (AfkMode && Watchdog.IsWatching(inst)) { Watchdog.Flag(inst.AccountRef, reason); return; }
        if (inst.Rejoins >= 3) { Log($"Not rejoining {inst.Label} again ({reason}) — it already failed 3 times.", Level.Warn); return; }
        _ = RejoinAsync(inst);
    }

    async Task RejoinAsync(RobloxInstance inst)
    {
        while (Launching) await Task.Delay(1000);
        Log($"Rejoining {inst.Label}…");
        try { using var p = Process.GetProcessById(inst.Pid); p.Kill(); } catch { }
        await Task.Delay(4000);
        if (Stats.Current is { } s) s.Rejoins++;
        await LaunchAsync(new[] { inst.AccountRef }, inst.Target.Clone(), null, inst.Rejoins + 1);
    }

    // ---------- accounts / launching ----------

    public Account AddOrUpdateAccount(long userId, string name, string displayName, string cookie)
    {
        var acc = Accounts.Accounts.FirstOrDefault(a => a.UserId == userId);
        if (acc == null)
        {
            acc = new Account { UserId = userId, AddedUtc = DateTime.UtcNow };
            Accounts.Accounts.Add(acc);
        }
        acc.Name = name;
        acc.DisplayName = displayName;
        acc.Secret = Secrets.Protect(cookie);
        SaveAccounts();
        Log($"Saved account {acc}.", Level.Good);
        return acc;
    }

    public void RemoveAccount(Account acc)
    {
        Accounts.Accounts.Remove(acc);
        SaveAccounts();
        Log($"Removed account {acc}.");
    }

    /// <param name="startAfk">Whether to turn AFK mode on first; null = the "Start AFK mode first" setting.</param>
    public async Task LaunchAsync(IList<Account> accounts, GameTarget target, IProgress<string> progress, int rejoins = 0, bool? startAfk = null)
    {
        if (Launching) { Log("A launch is already running.", Level.Warn); return; }
        Launching = true;
        int fpsBefore = 0;
        bool changedSettings = false;
        try
        {
            if ((startAfk ?? Cfg.AfkBeforeLaunch) && rejoins == 0 && !AfkMode) StartAfk();
            fpsBefore = SafeReadFps() ?? 0;

            _cancelLaunch = false;
            for (int i = 0; i < accounts.Count; i++)
            {
                if (_cancelLaunch) { Log($"Launch cancelled — {accounts.Count - i} account{(accounts.Count - i == 1 ? "" : "s")} not opened.", Level.Warn); break; }
                var acc = accounts[i];
                string step = accounts.Count > 1 ? $"[{i + 1}/{accounts.Count}] {acc.Name}" : acc.Name;
                try
                {
                    progress?.Report($"{step}: getting ready…");
                    string cookie = Secrets.Unprotect(acc.Secret);

                    if (target.PlaceId == 0 && target.ShareCode != null)
                    {
                        await RobloxApi.ResolveShareLink(cookie, target);
                        Log($"Share link → {target.Describe()}.");
                    }
                    if (!string.IsNullOrEmpty(target.LinkCode) && target.AccessCode == null)
                        target.AccessCode = await RobloxApi.TryGetAccessCode(cookie, target) ?? "";

                    if (Cfg.MultiInstance && Tracker.Alive.Count > 0) await ScanAsync();

                    var known = new HashSet<int>(Tracker.Alive.Select(x => x.Pid));
                    string ticket = await RobloxApi.GetAuthTicket(cookie, target.PlaceId);

                    int fps = acc.Fps > 0 ? acc.Fps : AfkMode ? Cfg.AfkFps : fpsBefore;
                    if (acc.Fps > 0 || acc.LowGraphics || Cfg.GraphicsBaseline != null)
                    {
                        ApplyLaunchSettings(acc, fps);
                        changedSettings = true;
                    }
                    Tracker.ExpectLaunch(acc, target, rejoins, fps, acc.LowGraphics);
                    Process.Start(new ProcessStartInfo(RobloxApi.BuildLaunchUri(ticket, target)) { UseShellExecute = true });
                    if (AfkMode) Watchdog.Watch(acc, target);
                    Log($"Launching {acc} → {target.Describe()}.");
                    if (!Cfg.HasLaunched) { Cfg.HasLaunched = true; SaveConfig(); }

                    progress?.Report($"{step}: waiting for Roblox to open…");
                    var inst = await WaitForNewInstance(known, TimeSpan.FromMinutes(2));
                    if (inst == null) { Log($"Roblox didn't open for {acc} within 2 minutes.", Level.Warn); continue; }

                    // Join together: public games put each account in a random server, so once the first account is
                    // in, send the rest to the exact server it joined (its server ID comes from Roblox's log).
                    if (i == 0 && accounts.Count > 1 && Cfg.JoinTogether && !target.IsPrivate && target.FollowUserId == 0 && string.IsNullOrEmpty(target.JobId))
                    {
                        progress?.Report($"{step}: waiting to see which server it joins…");
                        var until = DateTime.UtcNow.AddSeconds(120);
                        while (inst.JobId == null && !inst.Exited && DateTime.UtcNow < until) await Task.Delay(1000);
                        if (inst.JobId != null)
                        {
                            target = target.Clone();
                            target.JobId = inst.JobId;
                            if (inst.PlaceId > 0) target.PlaceId = inst.PlaceId;
                            Log($"{acc} is in — sending the other accounts to the same server.", Level.Good);
                        }
                        else Log("Couldn't see which server the first account joined, so the others may land in different servers.", Level.Warn);
                    }

                    if (changedSettings)
                    {
                        // Roblox reads its settings file while starting up. Don't touch the file again
                        // (for the next account, or to restore it) until this window has opened.
                        progress?.Report($"{step}: opening at {fps} FPS{(acc.LowGraphics ? ", lowest graphics" : "")}…");
                        var until = DateTime.UtcNow.AddSeconds(90);
                        while (inst.Hwnd == IntPtr.Zero && !inst.Exited && DateTime.UtcNow < until) await Task.Delay(500);
                        await Task.Delay(3000);
                    }

                    if (Cfg.MultiInstance && i < accounts.Count - 1)
                    {
                        progress?.Report($"{step}: unlocking multi-instance…");
                        for (int t = 0; t < 30 && !inst.EventCleared && !inst.Exited; t++)
                        {
                            await ScanAsync();
                            if (!inst.EventCleared) await Task.Delay(1000);
                        }
                        if (!inst.EventCleared)
                            Log($"Couldn't unlock multi-instance for {acc} yet; the next window may replace it.", Level.Warn);
                    }
                    if (i < accounts.Count - 1) await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    Log($"{acc}: {ex.Message}", Level.Bad);
                }
            }
        }
        finally
        {
            if (changedSettings)
            {
                RestoreSettings(AfkMode ? Cfg.AfkFps : fpsBefore);
                _restoreFpsPending = true;
                _noWindowsSince = DateTime.MaxValue;
            }
            Launching = false;
            progress?.Report("");
        }
    }

    void ApplyLaunchSettings(Account acc, int fps)
    {
        var values = new Dictionary<string, int>();
        if (fps > 0) values[FpsFile.Fps] = fps;
        if (acc.LowGraphics)
        {
            if (Cfg.GraphicsBaseline == null)
            {
                var current = FpsFile.ReadValues(FpsFile.SavedQuality, FpsFile.GraphicsLevel);
                if (current.Count == 2) { Cfg.GraphicsBaseline = current; SaveConfig(); }
            }
            foreach (var kv in FpsFile.LowestGraphics) values[kv.Key] = kv.Value;
        }
        else if (Cfg.GraphicsBaseline != null)
            foreach (var kv in Cfg.GraphicsBaseline) values[kv.Key] = kv.Value;
        FpsFile.WriteValues(values);
    }

    bool RestoreSettings(int fps)
    {
        try
        {
            var values = new Dictionary<string, int>();
            if (fps > 0) values[FpsFile.Fps] = fps;
            if (Cfg.GraphicsBaseline != null)
                foreach (var kv in Cfg.GraphicsBaseline) values[kv.Key] = kv.Value;
            if (values.Count == 0) return false;
            FpsFile.WriteValues(values);
            return true;
        }
        catch (Exception ex) { Log("Couldn't restore Roblox settings: " + ex.Message, Level.Warn); return false; }
    }

    async Task<RobloxInstance> WaitForNewInstance(HashSet<int> known, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var inst = Tracker.Alive.FirstOrDefault(x => !known.Contains(x.Pid));
            if (inst != null) return inst;
            await Task.Delay(500);
        }
        return null;
    }

    public void Dispose() => Dispose(byUser: true);

    /// <param name="byUser">false when Windows is shutting down/restarting: keep the AFK session so it can resume.</param>
    public void Dispose(bool byUser)
    {
        Engine.Dispose();
        Monitor.Dispose();
        StopAfk(byUser);
        Native.SetThreadExecutionState(ES_CONTINUOUS);
        SaveConfig();
        _breaker.Dispose();
    }
}

static class Format
{
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";

    public static string Clock(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    public static string Ago(DateTime? utc)
    {
        if (utc == null) return "—";
        var t = DateTime.UtcNow - utc.Value;
        return t.TotalSeconds < 60 ? "just now" : Duration(t) + " ago";
    }
}
