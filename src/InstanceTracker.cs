using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static AfkHub.Native;

namespace AfkHub;

sealed class RobloxInstance
{
    public int Pid;
    public IntPtr Hwnd;
    public DateTime StartedUtc;
    public Account AccountRef;
    public string CustomName;
    public GameTarget Target;
    public int Rejoins;
    public int LaunchFps;
    public bool LowGraphics;

    public string Status = "Starting";
    public bool Exited;
    public DateTime ExitedUtc;
    public string MultiState = "Waiting…";
    public bool EventCleared;
    public int EmptyScans;

    // Leftover-tray detection
    public bool HadWindow;
    public DateTime NoWindowSinceUtc;
    public bool? TrayLaunch;

    // Anti-AFK
    public bool AntiAfkOn = true;
    public DateTime LastActivityUtc;
    public DateTime? LastPressUtc;
    public int Presses;
    public bool Postponed;
    public bool ForceDue;

    // Log watching
    public string LogPath;
    public long LogPos;
    public int LogTries;
    public long PlaceId;
    public string JobId;
    public bool Disconnected;
    public int DisconnectCode;
    public DateTime DisconnectUtc;
    public bool DisconnectReported;
    public bool InCatchUp;

    /// <summary>When this window last joined a game (from Roblox's log), for knowing when it's safe to start a macro.</summary>
    public DateTime? JoinedUtc;
    public long RecordedPlace;
    public DateTime? HungSinceUtc;
    public bool FreezeHandled;
    /// <summary>Macro assignments follow the account across rejoins; unnamed windows are keyed by process.</summary>
    public string MacroKey => AccountRef != null ? "acct:" + AccountRef.UserId : "pid:" + Pid;

    public string Label => AccountRef?.ToString() ?? CustomName ??
        (Hwnd == IntPtr.Zero && Status == "Starting" ? $"Background Roblox (PID {Pid})" : $"Roblox (PID {Pid})");
}

sealed class InstanceTracker
{
    sealed record PendingLaunch(Account Account, GameTarget Target, DateTime Utc, int Rejoins, int Fps, bool LowGraphics);

    static readonly string LogsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
    static readonly Regex ThreadRx = new(@"^\d{4}-\d\d-\d\dT[\d:.]+Z,[\d.]+,([0-9a-f]+),", RegexOptions.Multiline);
    static readonly Regex JoinRx = new(@"! Joining game '([0-9a-fA-F-]+)' place (\d+)");
    static readonly Regex DisconnectRx = new(@"(?:Sending disconnect with reason|Disconnection Notification\. Reason|Lost connection with reason)\D{0,20}?(\d+)");

    readonly Dictionary<int, RobloxInstance> _map = new();
    readonly List<PendingLaunch> _pending = new();

    /// <summary>Every running RobloxPlayerBeta process, including the background tray one.</summary>
    public List<RobloxInstance> Alive { get; private set; } = new();
    /// <summary>Processes that have a visible game window.</summary>
    public List<RobloxInstance> Windows { get; private set; } = new();

    public event Action<RobloxInstance> Disconnected;
    public event Action<string> Log;

    public RobloxInstance Get(int pid) => _map.TryGetValue(pid, out var i) ? i : null;

    public void ExpectLaunch(Account account, GameTarget target, int rejoins, int fps, bool lowGraphics) =>
        _pending.Add(new PendingLaunch(account, target, DateTime.UtcNow, rejoins, fps, lowGraphics));

    public void Refresh(DateTime now)
    {
        _pending.RemoveAll(p => now - p.Utc > TimeSpan.FromMinutes(3));

        var alive = new List<RobloxInstance>();
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (p)
            {
                if (!_map.TryGetValue(p.Id, out var inst))
                {
                    inst = new RobloxInstance { Pid = p.Id, StartedUtc = StartTime(p) ?? now, LastActivityUtc = now, NoWindowSinceUtc = now };
                    var pend = _pending.FirstOrDefault(x => inst.StartedUtc >= x.Utc.AddSeconds(-5));
                    if (pend != null)
                    {
                        _pending.Remove(pend);
                        inst.AccountRef = pend.Account;
                        inst.Target = pend.Target;
                        inst.Rejoins = pend.Rejoins;
                        inst.LaunchFps = pend.Fps;
                        inst.LowGraphics = pend.LowGraphics;
                    }
                    _map[p.Id] = inst;
                }
                alive.Add(inst);
            }
        }

        var byPid = alive.ToDictionary(i => i.Pid);
        var found = new Dictionary<int, IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (byPid.ContainsKey((int)pid) && !found.ContainsKey((int)pid) &&
                (WindowTitle(h) == "Roblox" || WindowClass(h) == "WINDOWSCLIENT"))
                found[(int)pid] = h;
            return true;
        }, IntPtr.Zero);

        foreach (var inst in alive)
        {
            if (found.TryGetValue(inst.Pid, out var h))
            {
                if (inst.Hwnd == IntPtr.Zero)
                {
                    inst.LastActivityUtc = now;
                    if (inst.Status == "Starting") inst.Status = "Loading";
                }
                inst.Hwnd = h;
                inst.HadWindow = true;
            }
            else
            {
                if (inst.Hwnd != IntPtr.Zero) inst.NoWindowSinceUtc = now;
                inst.Hwnd = IntPtr.Zero;
            }
        }

        foreach (var inst in _map.Values.Where(i => !byPid.ContainsKey(i.Pid)).ToList())
        {
            if (!inst.Exited)
            {
                inst.Exited = true;
                inst.Hwnd = IntPtr.Zero;
                inst.ExitedUtc = now;
                inst.Status = "Closed";
                Log?.Invoke($"{inst.Label} closed.");
            }
            if (now - inst.ExitedUtc > TimeSpan.FromSeconds(15)) _map.Remove(inst.Pid);
        }

        Alive = alive;
        Windows = alive.Where(i => i.Hwnd != IntPtr.Zero).OrderBy(i => i.StartedUtc).ToList();
    }

    /// <summary>
    /// Roblox processes sitting in the tray with no game window: the "--launch-to-tray" startup copy, or a game
    /// Roblox hid instead of closing. Only ever picks windowless processes that have been that way for a while.
    /// </summary>
    public List<RobloxInstance> LeftoverTrayProcesses(DateTime now)
    {
        var result = new List<RobloxInstance>();
        foreach (var inst in Alive)
        {
            if (inst.Hwnd != IntPtr.Zero) continue;
            if (inst.HadWindow)
            {
                // A game the app launched for an account is never closed silently here: in AFK mode the
                // watchdog notices the hidden window, alerts you and rejoins it.
                if (inst.AccountRef != null) continue;
                if (now - inst.NoWindowSinceUtc > TimeSpan.FromSeconds(60)) result.Add(inst);
                continue;
            }
            if (now - inst.StartedUtc < TimeSpan.FromSeconds(20)) continue;
            inst.TrayLaunch ??= CommandLine(inst.Pid)?.IndexOf("--launch-to-tray", StringComparison.OrdinalIgnoreCase) >= 0;
            if (inst.TrayLaunch == true) result.Add(inst);
        }
        return result;
    }

    static string CommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementObject o in searcher.Get())
                using (o) return o["CommandLine"] as string;
        }
        catch { }
        return null;
    }

    static DateTime? StartTime(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); } catch { return null; }
    }

    // ---------- Log watching (join + disconnect detection) ----------

    public void UpdateLogs(DateTime now)
    {
        if (!Directory.Exists(LogsDir)) return;
        FileInfo[] files = null;

        foreach (var inst in Alive)
        {
            try
            {
                if (inst.LogPath == null && inst.LogTries < 60)
                {
                    inst.LogTries++;
                    files ??= new DirectoryInfo(LogsDir).GetFiles("*_Player_*_last.log")
                        .Where(f => f.Name.IndexOf("CrashHandler", StringComparison.OrdinalIgnoreCase) < 0).ToArray();
                    AttachLog(inst, files);
                }
                if (inst.LogPath != null) ReadNewLines(inst, now);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }

            if (inst.Disconnected && !inst.DisconnectReported && inst.DisconnectCode != 285 &&
                now - inst.DisconnectUtc > TimeSpan.FromSeconds(10))
            {
                // Waiting a few seconds avoids false alarms from teleports, which disconnect then immediately rejoin.
                inst.DisconnectReported = true;
                Disconnected?.Invoke(inst);
            }
        }
    }

    /// <summary>Each log line carries the writer's thread ID; match it to a live Roblox process's threads.</summary>
    void AttachLog(RobloxInstance inst, FileInfo[] files)
    {
        var taken = new HashSet<string>(_map.Values.Where(x => x.LogPath != null).Select(x => x.LogPath), StringComparer.OrdinalIgnoreCase);
        HashSet<int> threads;
        using (var p = Process.GetProcessById(inst.Pid))
            threads = new HashSet<int>(p.Threads.Cast<ProcessThread>().Select(t => t.Id));
        if (threads.Count == 0) return;

        foreach (var f in files.Where(f => !taken.Contains(f.FullName) && f.CreationTimeUtc >= inst.StartedUtc.AddSeconds(-60))
                               .OrderBy(f => f.CreationTimeUtc))
        {
            if (!LogThreadIds(f.FullName).Overlaps(threads)) continue;
            inst.LogPath = f.FullName;
            inst.LogPos = f.Length > (8 << 20) ? f.Length - (1 << 20) : 0;
            inst.InCatchUp = true;
            return;
        }
    }

    static HashSet<int> LogThreadIds(string path)
    {
        var ids = new HashSet<int>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        foreach (long start in new[] { 0L, Math.Max(0, fs.Length - 16384) })
        {
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[16384];
            int n = fs.Read(buf, 0, buf.Length);
            foreach (Match m in ThreadRx.Matches(Encoding.UTF8.GetString(buf, 0, n)))
                ids.Add(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber));
        }
        return ids;
    }

    void ReadNewLines(RobloxInstance inst, DateTime now)
    {
        using var fs = new FileStream(inst.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < inst.LogPos) inst.LogPos = 0;
        long available = fs.Length - inst.LogPos;
        if (available <= 0) { inst.InCatchUp = false; return; }

        int toRead = (int)Math.Min(available, 4 << 20);
        var buf = new byte[toRead];
        fs.Seek(inst.LogPos, SeekOrigin.Begin);
        int n = 0;
        while (n < toRead)
        {
            int r = fs.Read(buf, n, toRead - n);
            if (r <= 0) break;
            n += r;
        }

        int lastNewline = n > 0 ? Array.LastIndexOf(buf, (byte)'\n', n - 1) : -1;
        if (lastNewline < 0)
        {
            if (n == (4 << 20)) inst.LogPos += n; // one absurdly long line; skip it
            return;
        }
        inst.LogPos += lastNewline + 1;

        foreach (var line in Encoding.UTF8.GetString(buf, 0, lastNewline + 1).Split('\n'))
            ParseLine(inst, line, now);

        if (available <= toRead) inst.InCatchUp = false;
    }

    void ParseLine(RobloxInstance inst, string line, DateTime now)
    {
        if (line.IndexOf("! Joining game", StringComparison.Ordinal) >= 0)
        {
            var m = JoinRx.Match(line);
            if (!m.Success) return;
            inst.JobId = m.Groups[1].Value;
            inst.PlaceId = long.Parse(m.Groups[2].Value);
            inst.Status = "In game";
            if (!inst.InCatchUp) inst.JoinedUtc = now;
            else inst.JoinedUtc ??= now.AddMinutes(-1);
            inst.Disconnected = false;
            inst.DisconnectReported = false;
            if (!inst.InCatchUp) inst.LastActivityUtc = now;
            return;
        }

        if (line.IndexOf("isconnect", StringComparison.Ordinal) < 0 && line.IndexOf("Lost connection", StringComparison.Ordinal) < 0) return;
        var d = DisconnectRx.Match(line);
        if (!d.Success || inst.Disconnected) return;

        inst.Disconnected = true;
        inst.DisconnectCode = int.Parse(d.Groups[1].Value);
        inst.DisconnectUtc = now;
        inst.DisconnectReported = inst.InCatchUp; // don't alert for things that happened before the app was watching
        inst.Status = inst.DisconnectCode == 285 ? "Left game" : $"Disconnected ({DescribeCode(inst.DisconnectCode)})";
    }

    public static string DescribeCode(int code) => code switch
    {
        264 => "same account joined elsewhere",
        266 => "timed out",
        267 => "kicked",
        273 => "logged in elsewhere",
        277 => "lost connection",
        279 => "couldn't connect",
        _ => $"code {code}",
    };
}
