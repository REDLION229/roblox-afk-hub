using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace AfkHub;

/// <summary>An account AFK mode is supposed to keep in game.</summary>
sealed class WatchedAccount
{
    public Account Account;
    public GameTarget Target;
    /// <summary>"ok", "dropped" (auto-rejoin off), "rejoining" or "retrying" (quick retries used up).</summary>
    public string State = "ok";
    public string Problem;
    public DateTime? ProblemSinceUtc;
    public DateTime? LastDropUtc;
    public string LastProblem;
    /// <summary>A problem reported from outside (a frozen window), handled even though the window still looks alive.</summary>
    public string Forced;
    /// <summary>Has been seen in game since it was (re)launched; a window that then vanishes needs no startup grace.</summary>
    public bool SeenInGame;
    public int Attempts;
    /// <summary>Times it was relaunched this session (for the live status).</summary>
    public int Rejoins;
    public DateTime NextTryUtc;
    public DateTime GraceUntilUtc;
    public DateTime HealthySinceUtc;
    public long UserId => Account.UserId;
}

/// <summary>
/// Keeps every account you launched in AFK mode in game. Roblox's log only reports some disconnects;
/// a window can also crash, close, or get hidden without a word. So every few seconds this checks each
/// expected account has a live, visible, connected window — and if one doesn't, it alerts you and rejoins it.
/// </summary>
sealed class Watchdog
{
    /// <summary>Wait before each quick retry; after these it keeps trying every 15 minutes.</summary>
    static readonly TimeSpan[] Backoff = { TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) };
    static readonly TimeSpan SlowRetry = TimeSpan.FromMinutes(15);
    /// <summary>How long a freshly launched window gets to open and join before it counts as a problem.</summary>
    static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);

    readonly Hub _hub;
    readonly Dictionary<long, WatchedAccount> _watched = new();
    DateTime _lastCheck, _lastCheckin, _holdUntil;
    bool _recovering;

    public Watchdog(Hub hub) => _hub = hub;

    public List<WatchedAccount> Accounts => _watched.Values.ToList();
    public bool IsWatching(RobloxInstance inst) => inst.AccountRef != null && _watched.ContainsKey(inst.AccountRef.UserId);

    /// <summary>Starts (or keeps) watching an account; it gets a few minutes to get into the game first.</summary>
    public void Watch(Account account, GameTarget target)
    {
        if (account == null || target == null) return;
        if (!_watched.TryGetValue(account.UserId, out var w))
        {
            _watched[account.UserId] = w = new WatchedAccount { Account = account, HealthySinceUtc = DateTime.UtcNow };
            if (_watched.Count == 1) _lastCheckin = DateTime.UtcNow;
        }
        w.Target = target;
        w.GraceUntilUtc = DateTime.UtcNow + Grace;
    }

    public void Forget(long userId)
    {
        if (_watched.TryGetValue(userId, out var w) && _watched.Remove(userId))
            _hub.Log($"Stopped watching {w.Account} — it won't be rejoined until you launch it again.");
    }

    public void Clear() => _watched.Clear();

    /// <summary>Pauses checks while the app itself closes and reopens windows (rejoin timer).</summary>
    public void Hold(TimeSpan time) => _holdUntil = DateTime.UtcNow + time;

    /// <summary>Recover this account on the next check, even though its window still exists (e.g. it froze).</summary>
    public void Flag(Account account, string problem)
    {
        if (account == null || !_watched.TryGetValue(account.UserId, out var w)) return;
        w.Forced = problem;
        w.GraceUntilUtc = DateTime.MinValue;
        w.ProblemSinceUtc ??= DateTime.UtcNow.AddMinutes(-1);
        _lastCheck = DateTime.MinValue;
    }

    /// <summary>Check right away (a disconnect was just seen in Roblox's log).</summary>
    public void Nudge() => _lastCheck = DateTime.MinValue;

    public RobloxInstance WindowFor(WatchedAccount w) =>
        _hub.Tracker.Alive.Where(i => !i.Exited && i.AccountRef?.UserId == w.UserId).OrderByDescending(i => i.StartedUtc).FirstOrDefault();

    public static bool InGame(RobloxInstance i) => i.Hwnd != IntPtr.Zero && !i.Disconnected && (i.JoinedUtc != null || i.LogPath == null);

    /// <summary>Stricter, for saying "back in game": the join must be seen in Roblox's log (unless its log can't be found at all).</summary>
    static bool Joined(RobloxInstance i) => i.Hwnd != IntPtr.Zero && !i.Disconnected && (i.JoinedUtc != null || (i.LogPath == null && i.LogTries >= 60));

    /// <summary>What's wrong with this account right now, or null if it's fine.</summary>
    string Diagnose(WatchedAccount w, RobloxInstance inst, DateTime now)
    {
        if (w.Forced != null) return w.Forced;
        if (inst == null) return "its Roblox closed unexpectedly";
        if (inst.Disconnected) return inst.Status.StartsWith("Disconnected") ? inst.Status.ToLowerInvariant() : "it left the game";
        if (inst.Hwnd == IntPtr.Zero)
        {
            if (inst.HadWindow && now - inst.NoWindowSinceUtc > TimeSpan.FromSeconds(30)) return "its window disappeared (Roblox hid it or it crashed)";
            if (!inst.HadWindow && now > w.GraceUntilUtc) return "Roblox never opened a game window";
            return null;
        }
        // Only judge "never joined" when Roblox's log for this window is readable.
        if (inst.LogPath != null && inst.JoinedUtc == null && now > w.GraceUntilUtc) return "it didn't get into the game";
        return null;
    }

    public void Tick(DateTime now)
    {
        if (!_hub.AfkMode || _watched.Count == 0) return;
        CheckIn(now);
        if (now - _lastCheck < TimeSpan.FromSeconds(5) || _recovering || _hub.Launching || now < _holdUntil) return;
        _lastCheck = now;

        var due = new List<WatchedAccount>();
        foreach (var w in _watched.Values)
        {
            var inst = WindowFor(w);
            var problem = Diagnose(w, inst, now);

            if (problem == null)
            {
                w.ProblemSinceUtc = null;
                if (inst != null && Joined(inst)) w.SeenInGame = true;
                if (w.State != "ok" && inst != null && Joined(inst))
                {
                    w.State = "ok";
                    w.HealthySinceUtc = now;
                    _hub.Log($"{w.Account} is back in game.", Level.Good);
                    _hub.Notifier.Alert("Back in game", $"**{w.Account}** rejoined and is in game again{(w.Attempts > 1 ? $" (took {w.Attempts} tries)" : "")}.", Notifier.Green);
                }
                // Stayed in for a while: a later drop starts the quick retries from scratch.
                if (w.State == "ok" && w.Attempts > 0 && now - w.HealthySinceUtc > TimeSpan.FromMinutes(15)) w.Attempts = 0;
                continue;
            }

            // The startup grace is for windows still loading, not for one that was in game and then vanished.
            if (now < w.GraceUntilUtc && !(inst == null && w.SeenInGame)) continue;
            // Two checks in a row before acting, so a window that's just reloading isn't treated as gone.
            if (w.ProblemSinceUtc == null) { w.ProblemSinceUtc = now; w.Problem = problem; continue; }
            if (now - w.ProblemSinceUtc.Value < TimeSpan.FromSeconds(8)) continue;
            w.Problem = problem;

            if (w.State == "ok")
            {
                w.LastDropUtc = now;
                w.LastProblem = problem;
                if (_hub.Stats.Current is { } s) s.Disconnects++;
                bool rejoin = _hub.Cfg.AutoRejoin;
                w.State = rejoin ? "rejoining" : "dropped";
                w.NextTryUtc = now;
                _hub.Log($"{w.Account} dropped out: {problem}.{(rejoin ? " Rejoining it." : " Auto-rejoin is off.")}", Level.Bad);
                _hub.Notifier.Alert("Account dropped", $"**{w.Account}** — {problem}." + (rejoin ? " Rejoining it now." : " Auto-rejoin is off, so it needs a look."));
                _hub.RaiseAccountDropped(w.Account.ToString(), problem);
            }

            if (w.State == "dropped")
            {
                if (_hub.Cfg.AutoRejoin) w.State = "rejoining"; // switched on since
                else continue;
            }
            if (now >= w.NextTryUtc) due.Add(w);
        }

        if (due.Count > 0) _ = RecoverAsync(due);
    }

    async Task RecoverAsync(List<WatchedAccount> due)
    {
        _recovering = true;
        try
        {
            bool killed = false;
            foreach (var w in due)
            {
                w.Attempts++;
                if (WindowFor(w) is { } old)
                {
                    try { using var p = Process.GetProcessById(old.Pid); p.Kill(); killed = true; } catch { }
                }
                _hub.Log($"Rejoining {w.Account} (try {w.Attempts}) — {w.Problem}.", Level.Warn);
                if (w.Attempts == Backoff.Length + 1)
                {
                    w.State = "retrying";
                    _hub.Notifier.Alert("Still can't rejoin", $"**{w.Account}** couldn't get back in after {Backoff.Length} tries ({w.Problem}). It'll keep trying every {SlowRetry.TotalMinutes:0} minutes.", Notifier.Amber);
                }
                w.Forced = null;
                w.SeenInGame = false;
                w.Rejoins++;
                if (_hub.Stats.Current is { } s) s.Rejoins++;
            }
            if (killed) await Task.Delay(4000);

            // Accounts going to the same game launch together (so "Join together" keeps them in one server).
            foreach (var group in due.GroupBy(w => w.Target))
            {
                var target = group.Key.Clone();
                if (!target.IsPrivate && target.FollowUserId == 0)
                {
                    // Rejoin the server the other accounts are in right now; if that keeps failing, any server.
                    var sibling = _watched.Values.Except(group).Select(WindowFor).FirstOrDefault(i => i != null && InGame(i) && i.JobId != null);
                    if (sibling != null) target.JobId = sibling.JobId;
                    else if (group.Max(w => w.Attempts) >= 2) target.JobId = null;
                }
                var accounts = group.Select(w => w.Account).ToList();
                await _hub.LaunchAsync(accounts, target, null, group.Max(w => w.Attempts));
            }

            var now = DateTime.UtcNow;
            foreach (var w in due)
            {
                if (!_watched.ContainsKey(w.UserId)) continue; // AFK ended or you stopped watching it meanwhile
                w.GraceUntilUtc = now + Grace;
                w.ProblemSinceUtc = null;
                w.NextTryUtc = now + (w.Attempts < Backoff.Length ? Backoff[w.Attempts] : SlowRetry);
            }
        }
        catch (Exception ex) { _hub.Log("Rejoin failed: " + ex.Message, Level.Bad); }
        finally { _recovering = false; }
    }

    // ---------- reporting ----------

    void CheckIn(DateTime now)
    {
        int hours = _hub.Cfg.WebhookCheckinHours;
        if (hours <= 0 || now - _lastCheckin < TimeSpan.FromHours(hours)) return;
        _lastCheckin = now;
        int ok = _watched.Values.Count(w => w.State == "ok");
        var session = _hub.Stats.Current;
        _hub.Notifier.Checkin("AFK check-in",
            (session != null ? $"AFK for **{Format.Duration(session.Duration)}**\n" : "") +
            $"{ok}/{_watched.Count} accounts in game {(ok == _watched.Count ? "✅" : "⚠️")}\n" + StatusLines(),
            ok == _watched.Count ? Notifier.Green : Notifier.Amber);
    }

    /// <summary>One line per watched account, for Discord.</summary>
    public string StatusLines() => string.Join("\n", _watched.Values.Select(w =>
        w.State == "ok"
            ? $"✅ {w.Account}" + (w.LastDropUtc is { } d ? $" (dropped at {d.ToLocalTime():h:mm tt}, rejoined)" : "")
            : $"❌ {w.Account} — {Describe(w)}" + (w.LastDropUtc is { } d2 ? $" since {d2.ToLocalTime():h:mm tt}" : "")));

    public static string Describe(WatchedAccount w) => w.State switch
    {
        "rejoining" => $"rejoining (try {Math.Max(1, w.Attempts)}): {w.Problem}",
        "retrying" => $"still failing after {w.Attempts} tries, retrying every {SlowRetry.TotalMinutes:0} min: {w.Problem}",
        "dropped" => $"{w.Problem} (auto-rejoin is off)",
        _ => "in game",
    };
}
