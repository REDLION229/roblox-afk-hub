using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AfkHub;

/// <summary>
/// Phone alerts through a Discord webhook: a plain HTTPS POST to the channel's webhook link.
/// No bot, and nothing private is ever sent — only account display names, game names and counts.
/// </summary>
sealed class Notifier
{
    public const int Red = 0xF2555A, Green = 0x4CC38A, Blue = 0x4C8DFF, Amber = 0xF0B429;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly JavaScriptSerializer Json = new();
    readonly Config _cfg;
    DateTime _lastFailLog;

    public event Action<string> Problem;

    public Notifier(Config cfg) => _cfg = cfg;

    public static bool LooksValid(string url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme == "https" &&
        (u.Host == "discord.com" || u.Host == "discordapp.com" || u.Host.EndsWith(".discord.com")) &&
        u.AbsolutePath.StartsWith("/api/webhooks/");

    bool Enabled => LooksValid(_cfg.WebhookUrl);

    public void Alert(string title, string text, int color = Red) { if (_cfg.WebhookDisconnects) Send(title, text, color); }
    public void Action(string title, string text, int color = Blue) { if (_cfg.WebhookActions) Send(title, text, color); }
    public void Summary(string title, string text) { if (_cfg.WebhookSummary) Send(title, text, Green); }
    public void Checkin(string title, string text, int color) { if (_cfg.WebhookCheckinHours > 0) Send(title, text, color); }

    void Send(string title, string text, int color)
    {
        if (!Enabled) return;
        var url = _cfg.WebhookUrl.Trim();
        _ = Task.Run(async () =>
        {
            var error = await PostAsync(url, title, text, color);
            if (error != null && DateTime.UtcNow - _lastFailLog > TimeSpan.FromMinutes(10))
            {
                _lastFailLog = DateTime.UtcNow;
                Problem?.Invoke("Couldn't send the phone alert: " + error);
            }
        });
    }

    /// <summary>Sends one message; returns null on success or a short error.</summary>
    public static async Task<string> PostAsync(string url, string title, string text, int color)
    {
        if (!LooksValid(url)) return "That isn't a Discord webhook link.";
        var payload = Json.Serialize(new
        {
            username = "Roblox AFK Hub",
            embeds = new[] { new { title, description = text, color, timestamp = DateTime.UtcNow.ToString("o") } },
        });
        try
        {
            using var resp = await Http.PostAsync(url.Trim(), new StringContent(payload, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) return null;
            return (int)resp.StatusCode == 404 ? "Discord says that webhook doesn't exist anymore." : $"Discord answered HTTP {(int)resp.StatusCode}.";
        }
        catch (Exception ex) { return ex.Message; }
    }
}

/// <summary>
/// The "nobody's watching" features for an overnight PC:
/// resume after a restart, AFK schedule, rejoin timer, and frozen-window recovery.
/// Everything here is a cheap clock or window check, ticked once per second by the Hub.
/// </summary>
sealed class Automation
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "RobloxAfkHub";

    readonly Hub _hub;
    DateTime _lastSlow, _lastFreezeCheck, _lastGroupRejoin = DateTime.UtcNow;
    bool _resumeChecked;

    /// <summary>When an automatic resume will happen (null = none pending).</summary>
    public DateTime? ResumeAtUtc { get; private set; }

    public Automation(Hub hub) => _hub = hub;

    Config Cfg => _hub.Cfg;

    // ---------------------------------------------------------------- start with Windows

    public static void ApplyStartWithWindows(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (on) key.SetValue(RunName, $"\"{System.Windows.Forms.Application.ExecutablePath}\" --autostart");
        else key.DeleteValue(RunName, false);
    }

    public void CancelResume()
    {
        if (ResumeAtUtc == null) return;
        ResumeAtUtc = null;
        Cfg.ResumePending = false;
        _hub.SaveConfig();
        _hub.Log("Automatic resume cancelled.");
    }

    // ---------------------------------------------------------------- tick

    public void Tick(DateTime now)
    {
        if (!_resumeChecked)
        {
            _resumeChecked = true;
            if (Cfg.StartWithWindows) ApplyStartWithWindows(true); // keeps the path right if the app was moved
            if (Cfg.AutoResume && Cfg.ResumePending && !string.IsNullOrWhiteSpace(Cfg.LastLink) && Cfg.LastAccounts.Count > 0)
            {
                // Give Windows, the network and Discord a moment after a restart.
                ResumeAtUtc = now.AddSeconds(45);
                _hub.Log("Your AFK setup was still running when the PC restarted — resuming it in 45 seconds (you can cancel on Home).", Level.Warn);
            }
        }
        if (ResumeAtUtc is { } at && now >= at)
        {
            ResumeAtUtc = null;
            _ = ResumeAsync("after a restart");
        }

        if (Cfg.FreezeRecovery && now - _lastFreezeCheck >= TimeSpan.FromSeconds(5))
        {
            _lastFreezeCheck = now;
            CheckFrozen(now);
        }

        if (now - _lastSlow < TimeSpan.FromSeconds(20)) return;
        _lastSlow = now;
        CheckSchedule();
        CheckRejoinTimer(now);
    }

    async Task ResumeAsync(string why)
    {
        GameTarget target;
        try { target = GameLink.Parse(Cfg.LastLink); } catch { return; }
        var accounts = _hub.Accounts.Accounts.Where(a => Cfg.LastAccounts.Contains(a.UserId)).ToList();
        if (accounts.Count == 0) return;
        var macroKeys = Cfg.ResumeMacroKeys.ToList();
        _hub.Log($"Resuming your AFK setup {why}: {accounts.Count} account(s) → {target.Describe()}.", Level.Good);
        _hub.Notifier.Action("Resuming AFK", $"Relaunching {accounts.Count} account{(accounts.Count == 1 ? "" : "s")} {why}.");
        if (!_hub.AfkMode) _hub.StartAfk();
        await _hub.LaunchAsync(accounts, target, null);
        // Macros pick up again once their windows are in game (TickMacro handles the timing).
        foreach (var k in macroKeys) if (!Cfg.MacroRunKeys.Contains(k)) Cfg.MacroRunKeys.Add(k);
        _hub.SaveConfig();
    }

    // ---------------------------------------------------------------- frozen windows

    void CheckFrozen(DateTime now)
    {
        foreach (var w in _hub.Tracker.Windows)
        {
            if (!Native.IsHungAppWindow(w.Hwnd)) { w.HungSinceUtc = null; continue; }
            w.HungSinceUtc ??= now;
            if (now - w.HungSinceUtc.Value < TimeSpan.FromMinutes(Math.Max(1, Cfg.FreezeMinutes)) || w.FreezeHandled) continue;
            w.FreezeHandled = true;
            string mins = $"{Math.Max(1, Cfg.FreezeMinutes)} minute{(Cfg.FreezeMinutes == 1 ? "" : "s")}";
            if (w.AccountRef != null && w.Target != null)
            {
                _hub.Log($"{w.Label} has been frozen (not responding) for {mins} — closing it and rejoining.", Level.Bad);
                _hub.Notifier.Alert("Window froze", $"**{w.Label}** stopped responding for {mins}. Rejoining it.", Notifier.Amber);
                _hub.Rejoin(w, "it froze");
            }
            else
            {
                _hub.Log($"{w.Label} has been frozen for {mins} — closing it (it wasn't launched from the app, so it can't rejoin by itself).", Level.Bad);
                _hub.Notifier.Alert("Window froze", $"**{w.Label}** stopped responding for {mins} and was closed.", Notifier.Amber);
                try { using var p = Process.GetProcessById(w.Pid); p.Kill(); } catch { }
            }
        }
    }

    // ---------------------------------------------------------------- AFK schedule

    void CheckSchedule()
    {
        if (!Cfg.ScheduleOn) return;
        var local = DateTime.Now;
        string today = local.ToString("yyyy-MM-dd");
        if (Due(Cfg.ScheduleStart, local) && Cfg.ScheduleLastStart != today)
        {
            Cfg.ScheduleLastStart = today;
            _hub.SaveConfig();
            bool launch = Cfg.ScheduleLaunch && _hub.Tracker.Windows.Count == 0 && !_hub.Launching;
            _hub.Log($"Schedule: starting AFK mode ({Cfg.ScheduleStart}){(launch ? " and launching your last setup" : "")}.", Level.Good);
            _hub.Notifier.Action("Scheduled AFK started", launch ? "Launching your last setup." : "AFK mode is on.");
            if (launch) _ = ResumeAsync("on schedule");
            else if (!_hub.AfkMode) _hub.StartAfk();
        }
        if (Due(Cfg.ScheduleStop, local) && Cfg.ScheduleLastStop != today)
        {
            Cfg.ScheduleLastStop = today;
            _hub.SaveConfig();
            _hub.Log($"Schedule: ending AFK mode ({Cfg.ScheduleStop}){(Cfg.ScheduleCloseRoblox ? " and closing Roblox" : "")}.", Level.Good);
            _hub.StopMacro(null, "Macros stopped (schedule).");
            if (_hub.AfkMode) _hub.StopAfk();
            if (Cfg.ScheduleCloseRoblox)
                foreach (var w in _hub.Tracker.Windows.ToList())
                    try { using var p = Process.GetProcessById(w.Pid); p.Kill(); } catch { }
        }
    }

    /// <summary>True during the first 5 minutes after the given HH:mm today.</summary>
    static bool Due(string hhmm, DateTime local)
    {
        if (!TimeSpan.TryParse(hhmm, out var t)) return false;
        var at = local.Date + t;
        return local >= at && local < at.AddMinutes(5);
    }

    // ---------------------------------------------------------------- rejoin timer

    void CheckRejoinTimer(DateTime now)
    {
        if (Cfg.RejoinEveryHours <= 0 || _hub.Launching || now - _lastGroupRejoin < TimeSpan.FromMinutes(10)) return;
        var mine = _hub.Tracker.Windows.Where(w => w.AccountRef != null && w.Target != null && !w.Disconnected).ToList();
        if (mine.Count == 0) return;
        if (now - mine.Min(w => w.StartedUtc) < TimeSpan.FromHours(Cfg.RejoinEveryHours)) return;
        _lastGroupRejoin = now;
        _ = GroupRejoinAsync(mine);
    }

    async Task GroupRejoinAsync(List<RobloxInstance> windows)
    {
        var accounts = windows.Select(w => w.AccountRef).Distinct().ToList();
        var target = windows[0].Target.Clone();
        if (!target.IsPrivate && target.FollowUserId == 0) target.JobId = null; // fresh server; Join together keeps them together
        _hub.Log($"Rejoin timer: it's been {Cfg.RejoinEveryHours} h — rejoining {accounts.Count} account(s) into a fresh server.", Level.Good);
        _hub.Notifier.Action("Scheduled rejoin", $"Rejoining {accounts.Count} account{(accounts.Count == 1 ? "" : "s")} after {Cfg.RejoinEveryHours} h.");
        _hub.Watchdog.Hold(TimeSpan.FromSeconds(30)); // these windows closing on purpose isn't a drop
        foreach (var w in windows) try { using var p = Process.GetProcessById(w.Pid); p.Kill(); } catch { }
        await Task.Delay(5000);
        await _hub.LaunchAsync(accounts, target, null);
    }
}
