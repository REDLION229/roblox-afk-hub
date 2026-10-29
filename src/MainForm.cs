using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using static AfkHub.Native;

namespace AfkHub;

/// <summary>
/// Hosts the HTML interface (ui\index.html) in WebView2 and bridges it to the Hub engine.
/// The page is torn down whenever the app is hidden in the tray, so it costs nothing while AFK.
/// </summary>
sealed class MainForm : Form
{
    static readonly string UiDir = Embedded.UiDir;
    static readonly string WebDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AfkHub", "WebView2");
    static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };

    static readonly Dictionary<string, Color> ThemeBackgrounds = new()
    {
        ["midnight"] = Color.FromArgb(8, 9, 10),
        ["glass"] = Color.FromArgb(9, 13, 26),
        ["graphite"] = Color.FromArgb(22, 23, 26),
        ["ember"] = Color.FromArgb(18, 13, 11),
        ["paper"] = Color.FromArgb(244, 245, 247),
        ["avatar"] = Color.FromArgb(11, 12, 15),
    };

    readonly Hub _hub;
    readonly NotifyIcon _tray;
    readonly Timer _timer;
    readonly Dictionary<long, (string Text, string Kind)> _loginStatus = new();
    readonly Personal _personal;
    readonly Updater _updater = new();
    readonly DateTime _startedUtc = DateTime.UtcNow;
    readonly DiscordRpc _discord = new();
    DateTime _lastDiscord;
    readonly string _startPage, _snapshotPath;
    readonly bool _autostart;

    CoreWebView2Environment _env;
    WebView2 _web;
    bool _ready, _exiting, _creating;
    string _launchProgress = "";
    int _statsSentCount = -1;

    public MainForm()
    {
        _hub = new Hub();
        Text = "Roblox AFK Hub" + (Hub.IsAdmin ? "  (administrator)" : "");
        Icon = AppIcon.Get();
        MinimumSize = new Size(900, 600);
        // Fit smaller screens (e.g. a 1440×900 laptop) instead of opening off the bottom edge.
        var work = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(1240, work.Width - 40), Math.Min(820, work.Height - 40));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeBackgrounds.TryGetValue(_hub.Cfg.Theme ?? "", out var bg) ? bg : ThemeBackgrounds["midnight"];

        var args = Environment.GetCommandLineArgs();
        _autostart = args.Contains("--autostart");
        _startPage = ArgAfter(args, "--page");
        _snapshotPath = ArgAfter(args, "--snapshot");
        if (_snapshotPath != null)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-5000, -5000);
            ShowInTaskbar = false;
            if (ArgAfter(args, "--height") is { } h && int.TryParse(h, out var hv)) Size = new Size(Size.Width, hv);
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Roblox AFK Hub", null, (_, _) => ShowFromTray());
        menu.Items.Add("Start / stop AFK mode", null, (_, _) => ToggleAfk());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray = new NotifyIcon { Icon = Icon, Text = "Roblox AFK Hub", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

        _hub.Logged += (text, level) => Post(new { type = "log", entry = LogJson(new LogEntry(DateTime.Now, text, level)) });
        _hub.Disconnected += OnDisconnected;
        _hub.AccountDropped += OnAccountDropped;
        _hub.RecordingFinished += OnRecordingFinished;

        _timer = new Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();

        // Opening Roblox AFK Hub again while it's running brings this window back (see Program.Main).
        var showSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, Program.ShowSignalName);
        new System.Threading.Thread(() =>
        {
            while (showSignal.WaitOne())
                try { BeginInvoke(new Action(ShowFromTray)); } catch (InvalidOperationException) { return; }
        }) { IsBackground = true, Name = "show-signal" }.Start();

        _hub.Log(Hub.IsAdmin ? "Roblox AFK Hub started (administrator)." : "Roblox AFK Hub started.");
        if (args.Contains("--updated")) _hub.Log($"Updated to v{Program.Version}.", Level.Good);
        Updater.CleanUpOld();
        _personal = new Personal(_hub);
        _personal.Changed += () => { if (_ready) PushState(); };
        if (!_hub.Cfg.LiteMode && _hub.Cfg.ShowAvatar) _ = _personal.RefreshAvatarsAsync();
        Tick();
        _timer.Start();
    }

    static string ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override void SetVisibleCore(bool value)
    {
        // Started with Windows: go straight to the tray without flashing a window.
        if (_autostart && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await CreateWebAsync();
    }

    // ---------- WebView lifetime ----------

    async Task CreateWebAsync()
    {
        if (_web != null || _creating || !Visible || WindowState == FormWindowState.Minimized) return;
        _creating = true;
        try
        {
            var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            _web = web;
            // Keep WebView lean: one renderer, no background services it doesn't need.
            _env ??= await CoreWebView2Environment.CreateAsync(null, WebDataDir, new CoreWebView2EnvironmentOptions(
                "--renderer-process-limit=1 --disable-background-networking --disable-component-update --disable-features=msSmartScreenProtection,Translate"));
            await web.EnsureCoreWebView2Async(_env);
            if (!Visible || WindowState == FormWindowState.Minimized) { DestroyWeb(); return; }

            var core = web.CoreWebView2;
            bool dev = Environment.GetCommandLineArgs().Contains("--devtools");
            core.Settings.AreDevToolsEnabled = dev;
            core.Settings.AreDefaultContextMenusEnabled = dev;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = dev;
            core.SetVirtualHostNameToFolderMapping("afkhub.ui", UiDir, CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
            core.NavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith("https://afkhub.ui/", StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenExternal(a.Uri); }
            };
            core.Navigate("https://afkhub.ui/index.html" + (Environment.GetCommandLineArgs().Contains("--demo") ? "#demo," + (ArgAfter(Environment.GetCommandLineArgs(), "--theme") ?? "") : ""));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Roblox AFK Hub's interface needs the Microsoft Edge WebView2 Runtime, which couldn't start:\n\n" + ex.Message +
                "\n\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and open Roblox AFK Hub again.",
                "Roblox AFK Hub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _creating = false; }
    }

    void DestroyWeb()
    {
        if (_web == null) return;
        _ready = false;
        Controls.Remove(_web);
        _web.Dispose();
        _web = null;
        _statsSentCount = -1;
    }

    static void OpenExternal(string uri)
    {
        if (uri != null && (uri.StartsWith("https://") || uri.StartsWith("http://")))
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    void Post(object message)
    {
        if (!_ready || _web?.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(Json.Serialize(message)); } catch { }
    }

    void Toast(string text, string kind = "info") => Post(new { type = "toast", text, kind });

    // ---------- tick ----------

    bool _presetArgChecked;

    /// <summary>A minute after start, then twice a day: look for a new version (not in screenshot/test runs).</summary>
    void MaybeCheckForUpdate(DateTime now)
    {
        if (!_hub.Cfg.AutoUpdateCheck || _snapshotPath != null || _updater.Busy) return;
        if (now - _startedUtc < TimeSpan.FromMinutes(1)) return;
        if (_updater.LastCheckUtc != default && now - _updater.LastCheckUtc < TimeSpan.FromHours(12)) return;
        _ = CheckForUpdateAsync(manual: false);
    }

    async Task CheckForUpdateAsync(bool manual)
    {
        await _updater.CheckAsync();
        if (_updater.Available is { } rel && !manual && rel.Version != _hub.Cfg.SkipUpdateVersion)
            _hub.Log($"A new version is available: v{rel.Version}.", Level.Good);
        if (manual) Toast(_updater.Status, _updater.Available != null ? "good" : "info");
        PushState();
    }

    /// <summary>Never in the middle of AFKing: an update restarts the app, which would stop anti-AFK and the watchdog.</summary>
    async Task InstallUpdateAsync()
    {
        if (_hub.AfkMode) { Toast("Finish AFKing first (press \"I'm back\"), then update.", "warn"); return; }
        if (_hub.Launching || _hub.Engine.Active || _hub.RecordingStage != null) { Toast("Wait until launching, macros and recording are done, then update.", "warn"); return; }
        PushState();
        var error = await _updater.InstallAsync();
        if (error != null) { _hub.Log(error, Level.Bad); Toast(error, "bad"); PushState(); return; }
        _hub.Log(_updater.Status, Level.Good);
        Updater.Restart();
        _exiting = true;
        Close();
    }

    void Tick()
    {
        try { _hub.Tick(); }
        catch (Exception ex) { _hub.Log("Error: " + ex.Message, Level.Bad); }

        MaybeCheckForUpdate(DateTime.UtcNow);

        // --preset "Name": launch that preset on startup (handy for a desktop shortcut per game).
        if (!_presetArgChecked)
        {
            _presetArgChecked = true;
            // --self-update: check and install right away (used to test the updater without clicking).
            if (Environment.GetCommandLineArgs().Contains("--self-update"))
                _ = Task.Run(async () =>
                {
                    await _updater.CheckAsync();
                    _hub.Log("Self-update check: " + _updater.Status);
                    if (_updater.Available != null) BeginInvoke(new Action(() => _ = InstallUpdateAsync()));
                });
            if (ArgAfter(Environment.GetCommandLineArgs(), "--preset") is { } pn)
            {
                var p = _hub.Cfg.Presets.FirstOrDefault(x => x.Id == pn || string.Equals(x.Name, pn, StringComparison.OrdinalIgnoreCase));
                if (p != null) _ = LaunchPresetAsync(p);
                else _hub.Log($"There's no preset called \"{pn}\".", Level.Warn);
            }
        }

        var s = _hub.Stats.Current;
        _tray.Text = s != null ? $"Roblox AFK Hub — AFK {Format.Clock(s.Duration)}" : "Roblox AFK Hub";

        if (DateTime.UtcNow - _lastDiscord > TimeSpan.FromSeconds(15)) { _lastDiscord = DateTime.UtcNow; UpdateDiscord(); }

        if (_ready && Visible && WindowState != FormWindowState.Minimized)
        {
            PushState();
            // Self-throttled: presence every ~40 s, avatars every ~20 min.
            var c = _hub.Cfg;
            if (!c.LiteMode)
            {
                // Self-throttled: presence/friends about once a minute, avatars every ~20 min.
                if (c.ShowPresence) _ = _personal.RefreshPresenceAsync();
                if (c.ShowAvatar) _ = _personal.RefreshAvatarsAsync();
                if (c.ShowFriends) _ = _personal.RefreshFriendsAsync();
            }
            // Game names/icons (for recent games, macros and presets) only need a lookup per new game, so even Lite mode keeps it.
            _ = _personal.RefreshRecentAsync();
        }
    }

    void PushState()
    {
        Post(BuildState());
        if (_statsSentCount != _hub.Stats.Sessions.Count)
        {
            _statsSentCount = _hub.Stats.Sessions.Count;
            Post(new { type = "stats", sessions = _hub.Stats.Sessions.Take(400).Select(SessionJson).ToList() });
        }
    }

    object BuildState()
    {
        var cfg = _hub.Cfg;
        var now = DateTime.UtcNow;
        var alive = _hub.Tracker.Alive;
        string multi = !cfg.MultiInstance ? "off"
            : alive.Any(i => i.MultiState.StartsWith("Blocked")) ? "blocked"
            : alive.Count == 0 ? "ready"
            : alive.All(i => i.EventCleared) ? "unlocked" : "unlocking";
        var session = _hub.Stats.Current;
        int? fps = Hub.SafeReadFps();

        return new
        {
            type = "state",
            admin = Hub.IsAdmin,
            afk = _hub.AfkMode,
            antiAfk = cfg.AntiAfk,
            launching = _hub.Launching,
            launchProgress = _launchProgress,
            multi,
            multiCount = alive.Count(i => i.EventCleared),
            backgroundRoblox = alive.Count(i => i.Hwnd == IntPtr.Zero),
            intervalSeconds = (int)_hub.AntiAfk.Interval.TotalSeconds,
            fps = new { current = fps, locked = FpsFile.IsLocked, exists = FpsFile.Exists },
            session = session == null ? null : SessionJson(session),
            windows = _hub.Tracker.Windows.Select(w => WindowJson(w, now)).ToList(),
            watch = _hub.Watchdog.Accounts.Select(w => new
            {
                id = w.UserId.ToString(), name = w.Account.ToString(), ok = w.State == "ok", state = w.State,
                detail = Watchdog.Describe(w), hasWindow = _hub.Watchdog.WindowFor(w) is { Hwnd: var h } && h != IntPtr.Zero,
            }).ToList(),
            accounts = _hub.Accounts.Accounts.Select(AccountJson).ToList(),
            favorites = cfg.SavedGames.Select(g => new { name = g.Name, link = g.Link, game = g.Game, icon = g.Icon }).ToList(),
            presets = cfg.Presets.Select(PresetJson).ToList(),
            maxFavoritePresets = Presets.MaxFavorites,
            currentPlace = _hub.CurrentPlace().ToString(),
            personal = PersonalJson(),
            friends = _personal.FriendsInGame.Select(f => new
            {
                id = f.UserId.ToString(), name = f.Name, display = f.Display, headshot = f.Headshot,
                game = f.Game, icon = f.GameIcon, placeId = f.PlaceId.ToString(), canJoinServer = f.JobId != null, mine = f.Mine,
            }).ToList(),
            friendsLoaded = _personal.FriendsLoaded,
            friendCount = _personal.FriendCount,
            recent = cfg.RecentPlaces.Select(r => new { placeId = r.PlaceId.ToString(), game = r.Game, icon = r.Icon, ago = Format.Ago(r.LastUtc), joins = r.Joins }).ToList(),
            discordStatus = _discord.Status,
            resumeIn = _hub.Automation.ResumeAtUtc is { } ra ? Math.Max(0, (int)(ra - DateTime.UtcNow).TotalSeconds) : -1,
            webhookSet = Notifier.LooksValid(cfg.WebhookUrl),
            autostart = RobloxAutostart.Current() != null,
            dataDir = Store.Dir,
            version = Program.Version,
            update = new
            {
                available = _updater.Available?.Version,
                notes = _updater.Available?.Notes,
                skipped = _updater.Available != null && _updater.Available.Version == cfg.SkipUpdateVersion,
                status = _updater.Status,
                busy = _updater.Busy,
                auto = cfg.AutoUpdateCheck,
                upToDate = _updater.UpToDate,
                checkedAgo = _updater.LastCheckUtc == default ? null : Format.Ago(_updater.LastCheckUtc),
            },
            portable = Store.IsPortable,
            autostartRestorable = !string.IsNullOrEmpty(cfg.RobloxAutostartBackup),
            cfg = new
            {
                theme = cfg.Theme, reduceEffects = cfg.ReduceEffects,
                showAvatar = cfg.ShowAvatar, avatarEffects = cfg.AvatarEffects, mainAccount = _personal.MainAccountId.ToString(),
                liteMode = cfg.LiteMode, showFriends = cfg.ShowFriends, showPresence = cfg.ShowPresence,
                effectsOff = cfg.ReduceEffects || cfg.LiteMode,
                friendsOn = cfg.ShowFriends && !cfg.LiteMode, presenceOn = cfg.ShowPresence && !cfg.LiteMode,
                joinTogether = cfg.JoinTogether,
                webhookDisconnects = cfg.WebhookDisconnects, webhookActions = cfg.WebhookActions, webhookSummary = cfg.WebhookSummary,
                webhookCheckinHours = cfg.WebhookCheckinHours, webhookLiveStatus = cfg.WebhookLiveStatus, liveScreenshot = cfg.LiveScreenshot,
                startWithWindows = cfg.StartWithWindows, autoResume = cfg.AutoResume,
                freezeRecovery = cfg.FreezeRecovery, freezeMinutes = cfg.FreezeMinutes, rejoinEveryHours = cfg.RejoinEveryHours,
                scheduleOn = cfg.ScheduleOn, scheduleStart = cfg.ScheduleStart, scheduleStop = cfg.ScheduleStop,
                scheduleLaunch = cfg.ScheduleLaunch, scheduleCloseRoblox = cfg.ScheduleCloseRoblox, discordRpc = cfg.DiscordRpc, discordAppId = cfg.DiscordAppId ?? "",
                discordOnlyWhileAfk = cfg.DiscordOnlyWhileAfk, discordShowGame = cfg.DiscordShowGame, guideDismissed = cfg.GuideDismissed, hasLaunched = cfg.HasLaunched,
                mode = cfg.Mode, showMacros = cfg.ShowMacros,
                afkFps = cfg.AfkFps, normalFps = cfg.NormalFps, fpsPresets = cfg.FpsPresets,
                afkBeforeLaunch = cfg.AfkBeforeLaunch, multiInstance = cfg.MultiInstance, antiAfkMinutes = cfg.AntiAfkMinutes,
                antiAfkKey = cfg.AntiAfkKey, waitWhileBusy = cfg.WaitWhileBusy,
                alertOnDisconnect = cfg.AlertOnDisconnect, autoRejoin = cfg.AutoRejoin, minimizeToTray = cfg.MinimizeToTray,
                keepAwake = cfg.KeepAwake, keepScreenOn = cfg.KeepScreenOn,
                lastLink = cfg.LastLink ?? "", lastAccounts = cfg.LastAccounts.Select(x => x.ToString()).ToList(),
                macroPauseOnInput = cfg.MacroPauseOnInput, macroAutoResume = cfg.MacroAutoResume,
                macroResumeAfter = cfg.MacroResumeAfterSec, macroPinWindows = cfg.MacroPinWindows,
            },
            keys = AntiAfk.Keys.Select(k => new { id = k.Id, name = k.Name }).ToList(),
            macros = _hub.Macros.All.Select(MacroJson).ToList(),
            macro = MacroStateJson(),
        };
    }

    object MacroJson(MacroFile m) => new
    {
        id = m.Id,
        name = m.Name,
        source = m.Source,
        coords = m.Coords,
        seconds = Math.Round(m.DurationMs / 1000.0, 1),
        actions = m.Events.Count(e => e[1] != MacroFile.Move),
        created = m.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy"),
        placeId = m.PlaceId.ToString(),
        game = m.Game ?? _hub.GameName(m.PlaceId),
        gameIcon = m.PlaceId > 0 ? _hub.Cfg.RecentPlaces.FirstOrDefault(r => r.PlaceId == m.PlaceId)?.Icon : null,
    };

    object PresetJson(Preset p) => new
    {
        id = p.Id,
        name = p.Name,
        link = p.Link,
        game = p.Game,
        icon = p.Icon,
        favorite = p.Favorite,
        afk = p.Afk,
        lastUsed = p.LastUsedUtc == default ? null : Format.Ago(p.LastUsedUtc),
        accounts = p.Accounts.Select(pa =>
        {
            var acc = _hub.Accounts.Accounts.FirstOrDefault(a => a.UserId == pa.UserId);
            return new
            {
                id = pa.UserId.ToString(),
                name = acc == null ? "Removed account" : acc.DisplayName ?? acc.Name,
                missing = acc == null,
                avatar = acc == null ? null : _personal.SmallPicture(acc.UserId),
                fps = pa.Fps,
                low = pa.LowGraphics,
                macro = _hub.Macros.Get(pa.MacroId)?.Name,
                run = pa.RunMacro,
            };
        }).ToList(),
    };

    object MacroStateJson()
    {
        var e = _hub.Engine;
        var cfg = _hub.Cfg;
        var w = e.Window;
        string who = w?.Label ?? "its window";
        string name = e.Macro?.Name;
        if (name == null && cfg.MacroRunKeys.Count > 0 && cfg.MacroAssignments.TryGetValue(cfg.MacroRunKeys[0], out var a))
            name = _hub.Macros.Get(a.MacroId)?.Name;
        var rotation = cfg.MacroRunKeys
            .Select(k => _hub.Tracker.Windows.FirstOrDefault(x => x.MacroKey == k))
            .Where(x => x != null).ToList();
        var next = rotation.Count > 1 && w != null ? _hub.NextInRotation(w.MacroKey, DateTime.UtcNow) : null;
        int resumeIn = cfg.MacroAutoResume && _hub.Monitor.Running
            ? Math.Max(0, Math.Max(5, cfg.MacroResumeAfterSec) - _hub.Monitor.RealIdleMs / 1000) : -1;

        string text = e.State switch
        {
            MacroState.Off => "No macro running.",
            MacroState.WaitingWindow => $"Waiting for the window to be back in game, then “{name}” carries on.",
            MacroState.WaitingFocus => $"Trying to bring {who} to the front…",
            MacroState.Playing => rotation.Count > 1
                ? $"Taking turns: {who} is playing “{name}” (loop {Math.Min(e.Loops + 1, Math.Max(1, e.TurnLength))} of {e.TurnLength}), then {next?.Label ?? "the next window"}."
                : $"Playing “{name}” in {who}.",
            MacroState.TurnDone => $"Switching to {next?.Label ?? "the next window"}…",
            MacroState.Yielding => "Finishing this loop so anti-AFK can check the other windows…",
            MacroState.Yielded => "Paused for a moment while anti-AFK checks the other windows.",
            MacroState.PausedUser => resumeIn >= 0
                ? $"Paused because you used the PC. Resumes after {Math.Max(5, cfg.MacroResumeAfterSec)} s of no input ({resumeIn} s left)."
                : "Paused because you used the PC. Press Resume to continue.",
            MacroState.Finished => $"“{name}” played once and stopped (looping is off).",
            _ => e.State.ToString(),
        };
        return new
        {
            state = e.State.ToString(),
            text,
            problem = e.LastProblem,
            pid = w?.Pid,
            running = rotation.Count,
            nowPid = w?.Pid,
            nextPid = next?.Pid,
            name,
            loops = e.Loops,
            runSeconds = e.StartedUtc is { } st ? (int)(DateTime.UtcNow - st).TotalSeconds : 0,
            recording = _hub.RecordingStage,
            recordSeconds = _hub.RecordingStage == "recording" ? _hub.Recorder.ElapsedMs / 1000 : 0,
            recordEvents = _hub.RecordingStage == "recording" ? _hub.Recorder.EventCount : 0,
            recordWindow = _hub.Recorder.Window?.Label,
        };
    }

    // ---------- per-window details (process, game, health) ----------

    static readonly ulong TotalRam = Native.TotalRam();
    readonly Dictionary<int, (DateTime At, TimeSpan Cpu, double Pct)> _cpu = new();

    /// <summary>CPU % since the last reading (of the whole PC, like Task Manager), RAM, threads. Only read while the window is visible.</summary>
    (double Cpu, long RamMb, double RamPct, int Threads)? ProcessStats(RobloxInstance w, DateTime now)
    {
        try
        {
            using var p = Process.GetProcessById(w.Pid);
            double pct;
            // Readings closer than about a second apart are noisy, so reuse the last one.
            if (_cpu.TryGetValue(w.Pid, out var last) && (now - last.At).TotalMilliseconds < 900) pct = last.Pct;
            else
            {
                var total = p.TotalProcessorTime;
                pct = last.At == default ? 0
                    : Math.Max(0, (total - last.Cpu).TotalMilliseconds / (now - last.At).TotalMilliseconds / Environment.ProcessorCount * 100);
                if (_cpu.Count > 64) _cpu.Clear(); // old windows' entries
                _cpu[w.Pid] = (now, total, pct);
            }
            long ram = p.WorkingSet64;
            return (Math.Round(pct, 1), ram >> 20, TotalRam > 0 ? Math.Round(ram * 100.0 / TotalRam, 1) : 0, p.Threads.Count);
        }
        catch { return null; }
    }

    /// <summary>
    /// A simple 0–100 "how steady has this window been": full marks while it's in game and has never dropped,
    /// less for each rejoin this session, low while it's disconnected or frozen right now.
    /// </summary>
    (int Score, string Label, string Kind) Health(RobloxInstance w, DateTime now)
    {
        if (w.Disconnected) return (15, "Disconnected", "bad");
        if (w.HungSinceUtc is { } hung && now - hung > TimeSpan.FromSeconds(15)) return (25, "Not responding", "bad");
        if (w.JoinedUtc == null && w.LogPath != null) return (60, "Loading", "warn");
        int rejoins = _hub.Watchdog.Accounts.FirstOrDefault(x => x.UserId == w.AccountRef?.UserId)?.Rejoins ?? w.Rejoins;
        int score = Math.Max(40, 100 - rejoins * 15);
        return score >= 90 ? (score, "Stable", "good") : score >= 60 ? (score, "Rejoined " + rejoins + "×", "warn") : (score, "Shaky", "bad");
    }

    object WindowJson(RobloxInstance w, DateTime now)
    {
        int? next = null;
        string nextLabel;
        if (!_hub.Cfg.AntiAfk) nextLabel = "Anti-AFK paused";
        else if (!w.AntiAfkOn) nextLabel = "Off for this window";
        else if (w.Disconnected) nextLabel = "Disconnected";
        else if (w.ForceDue) nextLabel = "Pressing now…";
        else if (w.Postponed) nextLabel = "Waiting for you to stop typing";
        else
        {
            next = Math.Max(0, (int)(_hub.AntiAfk.Interval - (now - w.LastActivityUtc)).TotalSeconds);
            nextLabel = null;
        }

        var engine = _hub.Engine;
        bool macroHere = engine.Window == w && engine.State is MacroState.Playing or MacroState.Yielding or MacroState.Yielded;
        if (macroHere && w.AntiAfkOn && !w.Disconnected) { next = null; nextLabel = "Kept active by its macro"; }
        var assign = _hub.AssignmentFor(w);
        var assigned = _hub.Macros.Get(assign?.MacroId);

        var ps = ProcessStats(w, now);
        var health = Health(w, now);
        var place = w.PlaceId > 0 ? _hub.Cfg.RecentPlaces.FirstOrDefault(r => r.PlaceId == w.PlaceId) : null;

        string statusKind = w.Disconnected ? "bad" : w.Status == "In game" ? "good" : "neutral";
        string multiKind = w.EventCleared ? "good" : w.MultiState.StartsWith("Blocked") ? "bad" : "warn";
        return new
        {
            pid = w.Pid,
            placeId = w.PlaceId.ToString(),
            name = w.Label,
            userId = w.AccountRef?.UserId.ToString(),
            avatar = w.AccountRef != null ? _personal.SmallPicture(w.AccountRef.UserId) : null,
            fromApp = w.AccountRef != null,
            status = w.Status,
            statusKind,
            multi = w.MultiState,
            multiKind,
            fps = w.LaunchFps,
            low = w.LowGraphics,
            antiAfkOn = w.AntiAfkOn,
            next,
            nextLabel,
            presses = w.Presses,
            lastPress = w.LastPressUtc == null ? null : Format.Ago(w.LastPressUtc),
            uptime = (int)(now - w.StartedUtc).TotalSeconds,
            effectiveFps = _hub.WindowFps(w),
            details = new
            {
                cpu = ps?.Cpu, ramMb = ps?.RamMb, ramPct = ps?.RamPct, threads = ps?.Threads,
                placeId = w.PlaceId > 0 ? w.PlaceId.ToString() : null, jobId = w.JobId,
                game = place?.Game, gameIcon = place?.Icon,
                joined = w.JoinedUtc is { } j ? (int)(now - j).TotalSeconds : (int?)null,
                health = health.Score, healthLabel = health.Label, healthKind = health.Kind,
            },
            macro = assigned == null ? null : new
            {
                id = assigned.Id,
                name = assigned.Name,
                speed = assign.Speed,
                loop = assign.Loop,
                running = _hub.IsRunning(w),
                active = _hub.IsRunning(w) && engine.Window == w,
                loopsPerTurn = assign.LoopsPerTurn,
                totalLoops = (_hub.LoopTotals.TryGetValue(w.MacroKey, out var lt) ? lt : 0) + (engine.Window == w ? engine.Loops : 0),
            },
        };
    }

    readonly Dictionary<long, bool> _usableHere = new();

    object AccountJson(Account a)
    {
        if (!_usableHere.TryGetValue(a.UserId, out var usable)) _usableHere[a.UserId] = usable = Secrets.CanUnprotect(a.Secret);
        if (!usable) _loginStatus[a.UserId] = ("Saved on another PC — add again", "bad");
        _loginStatus.TryGetValue(a.UserId, out var st);
        return new
        {
            id = a.UserId.ToString(),
            name = a.Name,
            display = a.DisplayName,
            fps = a.Fps,
            low = a.LowGraphics,
            added = a.AddedUtc.ToLocalTime().ToString("MMM d, yyyy"),
            avatar = _personal.SmallPicture(a.UserId),
            presence = !_hub.Cfg.LiteMode && _hub.Cfg.ShowPresence && _personal.Presence.TryGetValue(a.UserId, out var pr) ? new { text = pr.Text, kind = pr.Kind, icon = pr.GameIcon } : null,
            main = a.UserId == _personal.MainAccountId,
            status = st.Text ?? "Saved",
            statusKind = st.Kind ?? "neutral",
        };
    }

    static object SessionJson(SessionRecord s) => new
    {
        start = (long)(s.StartUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
        seconds = (int)s.Duration.TotalSeconds,
        presses = s.Presses,
        disconnects = s.Disconnects,
        rejoins = s.Rejoins,
        maxWindows = s.MaxWindows,
        accounts = s.Accounts,
        inGame = s.InGameSeconds,
    };

    static object LogJson(LogEntry e) => new
    {
        time = e.Time.ToString("h:mm:ss tt"),
        text = e.Text,
        level = e.Level.ToString().ToLowerInvariant(),
    };

    object PersonalJson()
    {
        var cfg = _hub.Cfg;
        long mainId = _personal.MainAccountId;
        var acc = _hub.Accounts.Accounts.FirstOrDefault(a => a.UserId == mainId);
        var av = _personal.Get(mainId);
        _personal.Presence.TryGetValue(mainId, out var pr);
        return new
        {
            mainId = mainId == 0 ? null : mainId.ToString(),
            name = acc == null ? null : acc.DisplayName ?? acc.Name,
            username = acc?.Name,
            full = av?.Full,
            headshot = av?.Headshot,
            accent = av?.Accent,
            accentInk = av?.AccentInk,
            presence = pr == null || cfg.LiteMode || !cfg.ShowPresence ? null : new { text = pr.Text, kind = pr.Kind, icon = pr.GameIcon },
            showAvatar = cfg.ShowAvatar && !cfg.LiteMode,
            effects = cfg.AvatarEffects && !cfg.LiteMode && !cfg.ReduceEffects,
        };
    }

    // ---------- messages from the page ----------

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Dictionary<string, object> m;
        try { m = Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
        catch { return; }
        if (m == null || !m.TryGetValue("cmd", out var cmdObj)) return;
        try { HandleCommand(cmdObj as string, m); }
        catch (Exception ex) { Toast(ex.Message, "bad"); }
    }

    static string Str(Dictionary<string, object> m, string key) => m.TryGetValue(key, out var v) ? v?.ToString() : null;
    static bool Bool(Dictionary<string, object> m, string key) => m.TryGetValue(key, out var v) && v is bool b && b;
    static int Int(Dictionary<string, object> m, string key, int fallback = 0) =>
        m.TryGetValue(key, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : fallback;

    RobloxInstance Window(Dictionary<string, object> m) => _hub.Tracker.Get(Int(m, "pid"));
    Account AccountById(Dictionary<string, object> m) =>
        long.TryParse(Str(m, "id"), out var id) ? _hub.Accounts.Accounts.FirstOrDefault(a => a.UserId == id) : null;

    void HandleCommand(string cmd, Dictionary<string, object> m)
    {
        var cfg = _hub.Cfg;
        switch (cmd)
        {
            case "ready":
                _ready = true;
                Post(new { type = "init", page = _startPage, logs = _hub.RecentLog.Select(LogJson).ToList() });
                _statsSentCount = -1;
                PushState();
                if (_snapshotPath != null) _ = SnapshotAsync();
                if (Environment.GetCommandLineArgs().Contains("--test-login")) _ = TestLoginAsync();
                _ = _personal.RefreshFavoritesAsync();
                break;

            case "jsError":
                try { File.AppendAllText(Path.Combine(Store.Dir, "ui-errors.log"), $"{DateTime.Now:s}  {Str(m, "message")}\r\n{Str(m, "stack")}\r\n\r\n"); } catch { }
                return;

            case "toggleAfk": ToggleAfk(); break;
            case "toggleAntiAfk":
                cfg.AntiAfk = !cfg.AntiAfk;
                _hub.SaveConfig();
                _hub.Log(cfg.AntiAfk ? "Anti-AFK on." : "Anti-AFK paused.");
                break;

            case "setFps":
                int fps = Int(m, "value");
                if (fps < 1 || fps > 999) { Toast("FPS has to be between 1 and 999.", "warn"); break; }
                _hub.TrySetFps(fps);
                break;
            case "setLock":
                FpsFile.SetLocked(Bool(m, "on"));
                _hub.Log(Bool(m, "on") ? "Roblox settings file locked." : "Roblox settings file unlocked.");
                break;

            case "setConfig": SetConfig(Str(m, "key"), m.TryGetValue("value", out var val) ? val : null); break;
            case "setAutostart":
                if (Bool(m, "on"))
                {
                    if (!RobloxAutostart.Enable(cfg)) Toast("Nothing to restore — Roblox adds this back itself when it updates.", "warn");
                }
                else { RobloxAutostart.Disable(cfg); _hub.Log("Roblox won't start with Windows anymore.", Level.Good); }
                _hub.SaveConfig();
                break;

            case "restartAdmin":
                try { Hub.RestartAsAdmin(); _exiting = true; Application.Exit(); }
                catch (System.ComponentModel.Win32Exception) { /* UAC prompt cancelled */ }
                break;
            case "friendJoin":
                {
                    var f = _personal.FriendsInGame.FirstOrDefault(x => x.UserId.ToString() == Str(m, "userId"));
                    if (f == null) { Toast("They're not in a game anymore.", "warn"); break; }
                    // Their exact server if Roblox shared it, otherwise Roblox's "follow" join.
                    var target = f.JobId != null && f.PlaceId > 0
                        ? new GameTarget { PlaceId = f.PlaceId, JobId = f.JobId }
                        : new GameTarget { PlaceId = f.PlaceId, FollowUserId = f.UserId, FollowName = f.Display ?? f.Name };
                    _ = LaunchTargetAsync(target, m, $"Joining {f.Display ?? f.Name}");
                }
                break;
            case "testWebhook": _ = TestWebhookAsync(); break;
            case "cancelResume": _hub.Automation.CancelResume(); break;
            case "refreshFriends": _ = _personal.RefreshFriendsAsync(force: true); break;
            case "refreshAvatar": _ = _personal.RefreshAvatarsAsync(force: true); Toast("Refreshing your avatar…", "info"); break;
            case "quickRelaunch":
                if (string.IsNullOrWhiteSpace(cfg.LastLink) || cfg.LastAccounts.Count == 0) { Toast("Launch once from the Launch page first.", "warn"); break; }
                _ = LaunchAsync(new Dictionary<string, object> { ["link"] = cfg.LastLink, ["accounts"] = cfg.LastAccounts.Select(x => (object)x.ToString()).ToList() });
                break;
            case "openDataFolder": Directory.CreateDirectory(Store.Dir); Process.Start("explorer.exe", Store.Dir); break;
            case "openSettingsFile": Process.Start("notepad.exe", FpsFile.SettingsPath); break;
            case "updateCheck": _ = CheckForUpdateAsync(manual: true); break;
            case "updateInstall": _ = InstallUpdateAsync(); break;
            case "updateLater":
                if (_updater.Available is { } later) { cfg.SkipUpdateVersion = later.Version; _hub.SaveConfig(); }
                break;
            case "updatePage":
                if (_updater.Available?.Page is { } page) OpenExternal(page);
                break;
            case "openActivityLog":
                var logPath = Path.Combine(Store.Dir, "activity.log");
                if (File.Exists(logPath)) Process.Start("notepad.exe", logPath); else Toast("Nothing logged yet.", "info");
                break;
            case "makePortable":
                if (Store.IsPortable) { Toast("Roblox AFK Hub is already portable.", "info"); break; }
                Store.MakePortable();
                _hub.Log($"Copied settings, macros and stats to {Store.PortableDir}. Restarting to use it…", Level.Good);
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restart") { UseShellExecute = true });
                _exiting = true;
                Close();
                break;

            // windows
            case "winToggle": if (Window(m) is { } w1) w1.AntiAfkOn = !w1.AntiAfkOn; break;
            case "unwatch":
                if (long.TryParse(Str(m, "id"), out var uid)) _hub.Watchdog.Forget(uid);
                break;
            case "winPress":
                if (Window(m) is { } w2) { w2.ForceDue = true; _hub.Log($"Anti-AFK queued for {w2.Label}."); }
                break;
            case "winShow":
                if (Window(m) is { } w3 && w3.Hwnd != IntPtr.Zero)
                {
                    if (IsIconic(w3.Hwnd)) ShowWindow(w3.Hwnd, SW_RESTORE);
                    SetForegroundWindow(w3.Hwnd);
                }
                break;
            case "winRename":
                if (Window(m) is { } w4)
                {
                    if (w4.AccountRef != null) { Toast("This window came from a saved account, so it's already named.", "info"); break; }
                    var name = (Str(m, "name") ?? "").Trim();
                    w4.CustomName = name.Length == 0 ? null : name;
                }
                break;

            // macros
            case "macroAssign":
                if (Window(m) is { } mw)
                {
                    double speed = m.TryGetValue("speed", out var sp) && double.TryParse(sp?.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var sv) ? sv : 1.0;
                    _hub.AssignMacro(mw, Str(m, "macroId"), Math.Max(0.25, Math.Min(4, speed)), !m.ContainsKey("loop") || Bool(m, "loop"),
                        Int(m, "loopsPerTurn", 1));
                }
                break;
            case "macroStart": if (Window(m) is { } sw) _hub.StartMacro(sw); break;
            case "macroStop": _hub.StopMacro(m.ContainsKey("pid") ? Window(m) : null); break;
            case "macroResume": _hub.ResumeMacro(); break;
            case "macroRecord":
                if (Window(m) is { } rw && rw.Hwnd != IntPtr.Zero) _ = _hub.StartRecordingAsync(rw);
                else Toast("Pick an open Roblox window to record in.", "warn");
                break;
            case "macroRecordStop": _hub.FinishRecording(); break;
            case "macroImport": ImportTinyTask(); break;
            case "macroRename":
                if (_hub.Macros.Get(Str(m, "id")) is { } rm)
                {
                    var newName = (Str(m, "name") ?? "").Trim();
                    if (newName.Length > 0) { rm.Name = newName.Length > 60 ? newName.Substring(0, 60) : newName; _hub.Macros.Save(rm); }
                }
                break;
            case "macroDelete":
                if (_hub.Macros.Get(Str(m, "id")) is { } dm)
                {
                    foreach (var kv in _hub.Cfg.MacroAssignments.Where(kv => kv.Value.MacroId == dm.Id).ToList())
                    {
                        _hub.Cfg.MacroRunKeys.Remove(kv.Key);
                        _hub.Cfg.MacroAssignments.Remove(kv.Key);
                    }
                    _hub.Macros.Delete(dm);
                    _hub.SaveConfig();
                    _hub.Log($"Deleted macro \"{dm.Name}\".");
                }
                break;
            case "macroFit":
                if (_hub.Macros.Get(Str(m, "id")) is { } fm && Window(m) is { } fw)
                {
                    _hub.Macros.FitToWindow(fm, fw.Hwnd);
                    Toast($"\"{fm.Name}\" now follows the window, wherever it is.", "good");
                }
                break;

            // launching
            case "parseLink":
                try { Post(new { type = "parsed", ok = true, text = GameLink.Parse(Str(m, "link")).Describe() }); }
                catch (FormatException ex) { Post(new { type = "parsed", ok = false, text = ex.Message }); }
                break;
            case "launch": _ = LaunchAsync(m); break;
            case "saveFavorite":
                var link = (Str(m, "link") ?? "").Trim();
                GameLink.Parse(link);
                var favName = (Str(m, "name") ?? "").Trim();
                cfg.SavedGames.Add(new SavedGame { Name = favName.Length > 0 ? favName : GameLink.Parse(link).Describe(), Link = link });
                _hub.SaveConfig();
                Toast("Saved to favorites.", "good");
                _ = _personal.RefreshFavoritesAsync();
                break;
            case "removeFavorite":
                int idx = Int(m, "index", -1);
                if (idx >= 0 && idx < cfg.SavedGames.Count) { cfg.SavedGames.RemoveAt(idx); _hub.SaveConfig(); }
                break;

            // presets
            case "presetSave":
                {
                    // From the Launch page (link + ticked accounts), or "your current setup" (the last launch).
                    bool fromLast = Bool(m, "fromLast");
                    var plink = fromLast ? cfg.LastLink : (Str(m, "link") ?? "").Trim();
                    var pids = fromLast ? cfg.LastAccounts.ToHashSet() : IdSet(m);
                    var paccs = _hub.Accounts.Accounts.Where(a => pids.Contains(a.UserId)).ToList();
                    if (string.IsNullOrWhiteSpace(plink) || paccs.Count == 0) { Toast(fromLast ? "Launch once first, then save it." : "Pick a game and at least one account first.", "warn"); break; }
                    try { GameLink.Parse(plink); } catch (FormatException ex) { Toast(ex.Message, "warn"); break; }
                    var pname = (Str(m, "name") ?? "").Trim();
                    var preset = Presets.Capture(_hub, pname.Length > 0 ? pname : GameLink.Parse(plink).Describe(), plink, paccs);
                    preset.Favorite = cfg.Presets.Count(x => x.Favorite) < Presets.MaxFavorites;
                    cfg.Presets.Add(preset);
                    _hub.SaveConfig();
                    _hub.Log($"Saved preset \"{preset.Name}\" ({paccs.Count} account{(paccs.Count == 1 ? "" : "s")}).", Level.Good);
                    Toast($"Saved preset \"{preset.Name}\".", "good");
                    _ = _personal.RefreshFavoritesAsync();
                }
                break;
            case "presetUpdate":
                if (PresetById(m) is { } up)
                {
                    var uaccs = _hub.Accounts.Accounts.Where(a => cfg.LastAccounts.Contains(a.UserId)).ToList();
                    if (string.IsNullOrWhiteSpace(cfg.LastLink) || uaccs.Count == 0) { Toast("Launch once first, then update it.", "warn"); break; }
                    Presets.Capture(_hub, up.Name, cfg.LastLink, uaccs, up);
                    _hub.SaveConfig();
                    Toast($"\"{up.Name}\" now matches your current setup.", "good");
                    _ = _personal.RefreshFavoritesAsync();
                }
                break;
            case "presetLaunch":
                if (PresetById(m) is { } lp) _ = LaunchPresetAsync(lp);
                break;
            case "presetFavorite":
                if (PresetById(m) is { } fp)
                {
                    if (!fp.Favorite && cfg.Presets.Count(x => x.Favorite) >= Presets.MaxFavorites)
                    { Toast($"Up to {Presets.MaxFavorites} favorites, so Home stays tidy. Unstar one first.", "warn"); break; }
                    fp.Favorite = !fp.Favorite;
                    _hub.SaveConfig();
                }
                break;
            case "presetAfk":
                if (PresetById(m) is { } ap) { ap.Afk = !ap.Afk; _hub.SaveConfig(); }
                break;
            case "backAndClose": _hub.BackAndCloseAll(); break;
            case "chooseMode":
                {
                    // First run: sensible defaults for how you'll use it. Everything stays changeable in Settings.
                    bool afk = Str(m, "mode") == "afk";
                    cfg.Mode = afk ? "afk" : "play";
                    cfg.AfkBeforeLaunch = afk;
                    cfg.AutoRejoin = afk;
                    cfg.KeepAwake = afk;
                    cfg.ShowMacros = afk;
                    _hub.SaveConfig();
                    _hub.Log(afk ? "Set up for AFKing: AFK mode on launch, auto-rejoin and keep-awake are on." : "Set up for playing on several accounts: launching just opens the windows.", Level.Good);
                }
                break;
            case "arrangeWindows": _hub.ArrangeWindows(Str(m, "mode") != "stack"); break;
            case "presetRename":
                if (PresetById(m) is { } rp)
                {
                    var rn = (Str(m, "name") ?? "").Trim();
                    if (rn.Length > 0) { rp.Name = rn.Length > 60 ? rn.Substring(0, 60) : rn; _hub.SaveConfig(); }
                }
                break;
            case "presetDelete":
                if (PresetById(m) is { } dp) { cfg.Presets.Remove(dp); _hub.SaveConfig(); _hub.Log($"Deleted preset \"{dp.Name}\"."); }
                break;
            case "macroSetGame":
                if (_hub.Macros.Get(Str(m, "id")) is { } gm && long.TryParse(Str(m, "placeId"), out var gplace))
                {
                    _hub.TieToGame(gm, gplace);
                    _hub.Macros.Save(gm);
                }
                break;

            // accounts
            case "addLogin": _ = AddViaLoginAsync(); break;
            case "addCookie": _ = AddCookieAsync(RobloxApi.CleanCookie(Str(m, "cookie"))); break;
            case "checkLogins": _ = CheckLoginsAsync(); break;
            case "removeAccount":
                if (AccountById(m) is { } ra) _hub.RemoveAccount(ra);
                break;
            case "setAccountFps":
                if (AccountById(m) is { } fa)
                {
                    fa.Fps = Math.Max(0, Math.Min(999, Int(m, "fps")));
                    _hub.SaveAccounts();
                    _hub.Log(fa.Fps > 0 ? $"{fa} will open at {fa.Fps} FPS." : $"{fa} will open at your AFK FPS.");
                }
                break;
            case "setAccountLow":
                if (AccountById(m) is { } la)
                {
                    la.LowGraphics = Bool(m, "on");
                    _hub.SaveAccounts();
                    _hub.Log(la.LowGraphics ? $"{la} will open with the lowest graphics." : $"{la} will open with your normal graphics.");
                }
                break;
        }
        PushState();
    }

    void SetConfig(string key, object value)
    {
        var cfg = _hub.Cfg;
        bool b = value is bool bv && bv;
        int n = value != null && int.TryParse(value.ToString(), out var iv) ? iv : 0;
        switch (key)
        {
            case "theme":
                cfg.Theme = ThemeBackgrounds.ContainsKey(value as string ?? "") ? (string)value : "midnight";
                BackColor = ThemeBackgrounds[cfg.Theme];
                if (_web != null) _web.DefaultBackgroundColor = BackColor;
                break;
            case "reduceEffects": cfg.ReduceEffects = b; break;
            case "showAvatar": cfg.ShowAvatar = b; break;
            case "joinTogether": cfg.JoinTogether = b; break;
            case "webhookUrl":
                var hook = (value?.ToString() ?? "").Trim();
                if (hook.Length == 0) { cfg.WebhookUrl = null; Toast("Phone alerts link removed.", "info"); }
                else if (!Notifier.LooksValid(hook)) { Toast("That doesn't look like a Discord webhook link (it starts with https://discord.com/api/webhooks/).", "warn"); return; }
                else { cfg.WebhookUrl = hook; Toast("Webhook saved. Press \"Send test\" to check it.", "good"); }
                break;
            case "webhookDisconnects": cfg.WebhookDisconnects = b; break;
            case "webhookActions": cfg.WebhookActions = b; break;
            case "webhookSummary": cfg.WebhookSummary = b; break;
            case "startWithWindows": cfg.StartWithWindows = b; Automation.ApplyStartWithWindows(b); break;
            case "autoResume": cfg.AutoResume = b; break;
            case "freezeRecovery": cfg.FreezeRecovery = b; break;
            case "freezeMinutes": cfg.FreezeMinutes = Clamp(n, 1, 30); break;
            case "rejoinEveryHours": cfg.RejoinEveryHours = Clamp(n, 0, 48); break;
            case "scheduleOn": cfg.ScheduleOn = b; cfg.ScheduleLastStart = cfg.ScheduleLastStop = null; break;
            case "scheduleStart": if (TimeSpan.TryParse(value?.ToString(), out _)) cfg.ScheduleStart = value.ToString(); break;
            case "scheduleStop": if (TimeSpan.TryParse(value?.ToString(), out _)) cfg.ScheduleStop = value.ToString(); break;
            case "scheduleLaunch": cfg.ScheduleLaunch = b; break;
            case "scheduleCloseRoblox": cfg.ScheduleCloseRoblox = b; break;
            case "liteMode":
                cfg.LiteMode = b; _lastDiscord = DateTime.MinValue;
                _hub.Log(b ? "Lite mode on — extras paused; anti-AFK, multi-instance, launching and macros keep running." : "Lite mode off.", Level.Good);
                if (!b) _ = _personal.RefreshAvatarsAsync(force: true);
                break;
            case "showFriends": cfg.ShowFriends = b; break;
            case "showPresence": cfg.ShowPresence = b; break;
            case "discordRpc": cfg.DiscordRpc = b; _lastDiscord = DateTime.MinValue; break;
            case "discordAppId": cfg.DiscordAppId = new string((value?.ToString() ?? "").Where(char.IsDigit).ToArray()); _lastDiscord = DateTime.MinValue; break;
            case "discordOnlyWhileAfk": cfg.DiscordOnlyWhileAfk = b; _lastDiscord = DateTime.MinValue; break;
            case "discordShowGame": cfg.DiscordShowGame = b; _lastDiscord = DateTime.MinValue; break;
            case "avatarEffects": cfg.AvatarEffects = b; break;
            case "mainAccount":
                if (long.TryParse(value?.ToString(), out var mid)) { cfg.MainAccountId = mid; _hub.SaveConfig(); _ = _personal.RefreshAvatarsAsync(force: true); }
                break;
            case "guideDismissed": cfg.GuideDismissed = b; break;
            case "showMacros": cfg.ShowMacros = b; break;
            case "autoUpdateCheck": cfg.AutoUpdateCheck = b; break;
            case "afkFps": cfg.AfkFps = Clamp(n, 1, 999); break;
            case "normalFps": cfg.NormalFps = Clamp(n, 1, 999); break;
            case "afkBeforeLaunch": cfg.AfkBeforeLaunch = b; break;
            case "multiInstance": cfg.MultiInstance = b; break;
            case "antiAfkMinutes": cfg.AntiAfkMinutes = Clamp(n, 1, 19); break;
            case "antiAfkKey": if (AntiAfk.Keys.Any(k => k.Id == value as string)) cfg.AntiAfkKey = (string)value; break;
            case "waitWhileBusy": cfg.WaitWhileBusy = b; break;
            case "alertOnDisconnect": cfg.AlertOnDisconnect = b; break;
            case "autoRejoin": cfg.AutoRejoin = b; break;
            case "webhookCheckinHours": cfg.WebhookCheckinHours = Clamp(n, 0, 24); break;
            case "webhookLiveStatus": cfg.WebhookLiveStatus = b; break;
            case "liveScreenshot": cfg.LiveScreenshot = b; break;
            case "minimizeToTray": cfg.MinimizeToTray = b; break;
            case "keepAwake": cfg.KeepAwake = b; break;
            case "keepScreenOn": cfg.KeepScreenOn = b; break;
            case "macroPauseOnInput": cfg.MacroPauseOnInput = b; break;
            case "macroAutoResume": cfg.MacroAutoResume = b; break;
            case "macroResumeAfter": cfg.MacroResumeAfterSec = Clamp(n, 5, 3600); break;
            case "macroPinWindows": cfg.MacroPinWindows = b; break;
            default: return;
        }
        _hub.SaveConfig();
    }

    static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

    Preset PresetById(Dictionary<string, object> m) => _hub.Cfg.Presets.FirstOrDefault(p => p.Id == Str(m, "id"));

    static HashSet<long> IdSet(Dictionary<string, object> m) =>
        (m.TryGetValue("accounts", out var a) ? a as IEnumerable : null)?.Cast<object>()
            .Select(x => long.TryParse(x?.ToString(), out var id) ? id : 0).ToHashSet() ?? new HashSet<long>();

    /// <summary>Puts the preset's settings in place, launches its accounts, then starts its macros once they're in game.</summary>
    async Task LaunchPresetAsync(Preset p)
    {
        if (_hub.Launching) { Toast("A launch is already running.", "warn"); return; }
        GameTarget target;
        try { target = GameLink.Parse(p.Link); }
        catch (FormatException ex) { Toast(ex.Message, "warn"); return; }
        var (accounts, runKeys) = Presets.Apply(_hub, p);
        if (accounts.Count == 0) { Toast("None of this preset's accounts are saved anymore.", "warn"); return; }

        _hub.Log($"Launching preset \"{p.Name}\": {accounts.Count} account{(accounts.Count == 1 ? "" : "s")}{(runKeys.Count > 0 ? $", {runKeys.Count} macro{(runKeys.Count == 1 ? "" : "s")}" : "")}.", Level.Good);
        Toast($"Launching \"{p.Name}\"…", "info");
        PushState();
        await _hub.LaunchAsync(accounts, target, new Progress<string>(s => { _launchProgress = s; PushState(); }), startAfk: p.Afk);
        _launchProgress = "";
        // Each macro starts by itself once its window has been in game for a few seconds (see Hub.TickMacro).
        foreach (var k in runKeys) if (!_hub.Cfg.MacroRunKeys.Contains(k)) { _hub.Cfg.MacroRunKeys.Add(k); _hub.LoopTotals[k] = 0; }
        if (runKeys.Count > 0) _hub.SaveConfig();
        Toast($"\"{p.Name}\" launched.{(runKeys.Count > 0 ? " Macros start once their windows are in game." : "")}", "good");
        PushState();
    }

    async Task LaunchAsync(Dictionary<string, object> m)
    {
        GameTarget target;
        try { target = GameLink.Parse(Str(m, "link")); }
        catch (FormatException ex) { Toast(ex.Message, "warn"); return; }

        var ids = IdSet(m);
        var selected = _hub.Accounts.Accounts.Where(x => ids.Contains(x.UserId)).ToList();
        if (selected.Count == 0) { Toast("Pick at least one account to launch.", "warn"); return; }

        _hub.Cfg.LastLink = Str(m, "link").Trim();
        _hub.Cfg.LastAccounts = selected.Select(x => x.UserId).ToList();
        _hub.SaveConfig();

        await _hub.LaunchAsync(selected, target, new Progress<string>(s => { _launchProgress = s; PushState(); }));
        _launchProgress = "";
        Toast(selected.Count == 1 ? "Launch finished." : $"Launched {selected.Count} accounts.", "good");
        PushState();
    }

    /// <summary>Launch the accounts listed in m["accounts"] into an already-built target (friend join etc.).</summary>
    async Task LaunchTargetAsync(GameTarget target, Dictionary<string, object> m, string what)
    {
        var ids = (m.TryGetValue("accounts", out var a) ? a as IEnumerable : null)?.Cast<object>()
            .Select(x => long.TryParse(x?.ToString(), out var id) ? id : 0).ToHashSet() ?? new HashSet<long>();
        var selected = _hub.Accounts.Accounts.Where(x => ids.Contains(x.UserId)).ToList();
        if (selected.Count == 0) { Toast("Tick at least one account on the Launch page first.", "warn"); return; }
        Toast($"{what} with {selected.Count} account{(selected.Count == 1 ? "" : "s")}…", "info");
        await _hub.LaunchAsync(selected, target, new Progress<string>(s => { _launchProgress = s; PushState(); }));
        _launchProgress = "";
        PushState();
    }

    /// <summary>
    /// --test-login: opens the login window off-screen four times in a row (adding several accounts in one go),
    /// reports whether Roblox's page started loading each time, then exits.
    /// </summary>
    async Task TestLoginAsync()
    {
        await Task.Yield(); // same as AddViaLoginAsync: never open the dialog inside the page's message
        var results = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            // The 4th uses the separate throwaway browser the login window falls back to.
            // Opened as a dialog, exactly like the Add account button does.
            using var f = new LoginForm(i < 3 ? _env : null) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
            var closer = new Timer { Interval = 12000 };
            closer.Tick += (_, _) => { closer.Stop(); f.Close(); };
            closer.Start();
            f.ShowDialog(this);
            closer.Dispose();
            results.Add((f.Started ? "OK" : "FAILED " + (f.Error ?? "never loaded")) + (f.FirstError != null ? " (after fallback)" : ""));
            await Task.Delay(1500);
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "afkhub-login-test.txt"), string.Join(" | ", results));
        _exiting = true;
        Close();
    }

    async Task TestWebhookAsync()
    {
        if (!Notifier.LooksValid(_hub.Cfg.WebhookUrl)) { Toast("Paste your Discord webhook link first.", "warn"); return; }
        var error = await Notifier.PostAsync(_hub.Cfg.WebhookUrl, "Test from Roblox AFK Hub",
            "Phone alerts are working. You'll get a message here when an account disconnects, rejoins, or a session ends.", Notifier.Blue);
        Toast(error == null ? "Test sent — check your Discord channel." : "Didn't work: " + error, error == null ? "good" : "bad");
    }

    void UpdateDiscord()
    {
        var cfg = _hub.Cfg;
        string appId = cfg.DiscordRpc && !cfg.LiteMode ? cfg.DiscordAppId : null;
        if (string.IsNullOrWhiteSpace(appId)) { _discord.Update(null, null); return; }

        var wins = _hub.Tracker.Windows;
        var session = _hub.Stats.Current;
        if (cfg.DiscordOnlyWhileAfk && session == null && wins.Count == 0) { _discord.Update(appId, null); return; }

        long place = wins.Select(w => w.PlaceId).FirstOrDefault(p => p > 0);
        var gi = place > 0 ? _personal.CachedPlace(place) : null;
        if (place > 0 && gi == null) _ = _personal.PlaceInfo(place); // name shows up on the next update
        string game = cfg.DiscordShowGame ? gi?.Name : null;
        int macros = cfg.MacroRunKeys.Count;

        string details = wins.Count == 0 ? "Getting ready to AFK" : game != null ? $"AFK in {game}" : "AFK in Roblox";
        string state = wins.Count == 0 ? "Anti-AFK on standby"
            : $"{wins.Count} account{(wins.Count == 1 ? "" : "s")} · anti-AFK on" + (macros > 0 ? $" · {macros} macro{(macros == 1 ? "" : "s")}" : "");
        var start = session?.StartUtc ?? (wins.Count > 0 ? wins.Min(w => w.StartedUtc) : DateTime.UtcNow);

        var main = _personal.Get(_personal.MainAccountId);
        var acc = _hub.Accounts.Accounts.FirstOrDefault(x => x.UserId == _personal.MainAccountId);
        var activity = new Dictionary<string, object>
        {
            ["details"] = details.Length > 120 ? details.Substring(0, 120) : details,
            ["state"] = state,
            ["timestamps"] = new Dictionary<string, object> { ["start"] = (long)(start - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds },
        };
        var assets = new Dictionary<string, object>();
        var large = (cfg.DiscordShowGame ? gi?.Icon : null) ?? main?.Full;
        if (large != null) { assets["large_image"] = large; assets["large_text"] = game ?? "Roblox AFK Hub"; }
        if (main?.Headshot != null) { assets["small_image"] = main.Headshot; assets["small_text"] = acc?.DisplayName ?? acc?.Name ?? "Roblox AFK Hub"; }
        if (assets.Count > 0) activity["assets"] = assets;
        if (cfg.DiscordShowGame && place > 0)
            activity["buttons"] = new[] { new Dictionary<string, object> { ["label"] = "View game", ["url"] = $"https://www.roblox.com/games/{place}" } };
        _discord.Update(appId, activity);
    }

    async Task AddViaLoginAsync()
    {
        // This is called from the page's click message. Opening a dialog while that message is still being
        // handled freezes WebView2, so the login window's browser would never start: let the message finish first.
        await Task.Yield();
        string cookie;
        try
        {
            using var f = new LoginForm(_env);
            var result = f.ShowDialog(this);
            if (f.FirstError != null) _hub.Log($"Login window: the app's browser refused ({f.FirstError}), so a separate one was used.", Level.Warn);
            if (f.Error != null) _hub.Log($"Login window couldn't start: {f.Error}", Level.Bad);
            if (result != DialogResult.OK) return;
            cookie = f.Cookie;
        }
        catch (Exception ex) { Toast("The login window couldn't start: " + ex.Message, "bad"); return; }
        await AddCookieAsync(cookie);
    }

    async Task AddCookieAsync(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return;
        try
        {
            var u = await RobloxApi.GetUser(cookie);
            _hub.AddOrUpdateAccount(u.Id, u.Name, u.DisplayName, cookie);
            _usableHere[u.Id] = true;
            _loginStatus[u.Id] = ("Logged in", "good");
            Toast($"Added {u.DisplayName ?? u.Name}.", "good");
            _ = _personal.RefreshAvatarsAsync(force: true);
        }
        catch (Exception ex) { Toast("Couldn't add that account: " + ex.Message, "bad"); }
        PushState();
    }

    async Task CheckLoginsAsync()
    {
        foreach (var a in _hub.Accounts.Accounts.ToList())
        {
            _loginStatus[a.UserId] = ("Checking…", "neutral");
            PushState();
            try
            {
                var u = await RobloxApi.GetUser(Secrets.Unprotect(a.Secret));
                if (u.Name != a.Name || u.DisplayName != a.DisplayName) { a.Name = u.Name; a.DisplayName = u.DisplayName; _hub.SaveAccounts(); }
                _loginStatus[a.UserId] = ("Logged in", "good");
            }
            catch (RobloxAuthException) { _loginStatus[a.UserId] = ("Expired — add it again", "bad"); }
            catch (Exception ex) { _loginStatus[a.UserId] = ("Error: " + ex.Message, "warn"); }
        }
        _ = _personal.RefreshAvatarsAsync(force: true);
        PushState();
    }

    void ImportTinyTask()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import a TinyTask recording",
            Filter = "TinyTask recordings (*.rec)|*.rec|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var mf = _hub.Macros.ImportTinyTask(dlg.FileName);
            // If every open window is in the same game, that's the game it's for (changeable on the Macros page).
            if (_hub.CurrentPlace() is var place and > 0) { _hub.TieToGame(mf, place); _hub.Macros.Save(mf); }
            _hub.Log($"Imported \"{mf.Name}\" from TinyTask ({mf.Events.Count(e => e[1] != MacroFile.Move)} actions, {mf.DurationMs / 1000.0:0.#} s).", Level.Good);
            Post(new { type = "imported", macro = MacroJson(mf) });
        }
        catch (Exception ex) { Toast("Couldn't import that file: " + ex.Message, "bad"); }
    }

    void OnRecordingFinished(MacroFile m)
    {
        // Come back to the front so you can name the new macro.
        ShowFromTray();
        InputSender.AltUnlock();
        SetForegroundWindow(Handle);
        PushState();
        if (m != null) Post(new { type = "recorded", macro = MacroJson(m) });
    }

    // ---------- AFK / tray / alerts ----------

    void ToggleAfk()
    {
        if (_hub.AfkMode) _hub.StopAfk(); else _hub.StartAfk();
        PushState();
    }

    void OnDisconnected(RobloxInstance inst)
    {
        Toast($"{inst.Label}: {inst.Status}", "bad");
        if (!_hub.Cfg.AlertOnDisconnect) return;
        SystemSounds.Exclamation.Play();
        _tray.ShowBalloonTip(15000, "Roblox disconnected", $"{inst.Label}: {inst.Status}", ToolTipIcon.Warning);
    }

    void OnAccountDropped(string account, string problem)
    {
        Toast($"{account}: {problem}", "bad");
        if (!_hub.Cfg.AlertOnDisconnect) return;
        SystemSounds.Exclamation.Play();
        _tray.ShowBalloonTip(15000, "Account dropped out", $"{account}: {problem}", ToolTipIcon.Warning);
    }

    async void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        await CreateWebAsync();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        try { if (_web?.CoreWebView2 != null) _web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // While you're in Roblox or another app, let WebView trim its memory.
        try { if (_web?.CoreWebView2 != null) _web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
    }

    bool _suspended;

    protected override async void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Minimized to the taskbar: hide and freeze the interface so it uses no CPU until you bring it back.
        if (WindowState == FormWindowState.Minimized && _hub?.Cfg.MinimizeToTray == false && _web?.CoreWebView2 != null && !_suspended)
        {
            _suspended = true;
            _web.Visible = false;
            try { await _web.CoreWebView2.TrySuspendAsync(); } catch { }
            return;
        }
        if (WindowState != FormWindowState.Minimized && _suspended && _web != null)
        {
            _suspended = false;
            _web.Visible = true; // visible again = resumed
            PushState();
        }
        if (WindowState != FormWindowState.Minimized && Visible && _web == null) await CreateWebAsync();
        if (WindowState == FormWindowState.Minimized && _hub?.Cfg.MinimizeToTray == true && _snapshotPath == null)
        {
            Hide();
            // The interface isn't needed in the tray: unload it and hand the memory back to Roblox.
            DestroyWeb();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            _tray.ShowBalloonTip(2500, "Roblox AFK Hub is still running", "Double-click the tray icon to open it.", ToolTipIcon.None);
        }
    }

    async Task SnapshotAsync()
    {
        await Task.Delay(int.TryParse(ArgAfter(Environment.GetCommandLineArgs(), "--wait"), out var wait) ? wait : 2500);
        using (var fs = File.Create(_snapshotPath))
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _hub.AfkMode)
        {
            var r = MessageBox.Show(this,
                "AFK mode is on. Closing Roblox AFK Hub stops anti-AFK and puts your normal FPS back.\n\nClose anyway?",
                "Roblox AFK Hub", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
        }
        _timer.Stop();
        _tray.Visible = false;
        DestroyWeb();
        _discord.Dispose();
        _hub.Dispose(byUser: e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}

static class AppIcon
{
    static Icon _icon;

    public static Icon Get()
    {
        if (_icon != null) return _icon;
        try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { _icon = SystemIcons.Application; }
        return _icon;
    }
}
