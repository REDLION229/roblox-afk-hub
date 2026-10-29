using System;
using System.Collections.Generic;
using System.Linq;

namespace AfkHub;

/// <summary>
/// A saved setup for one game: where to join, which accounts, how each account's window opens,
/// and which macro each one runs. Launching it puts all of that back and opens everything in one go.
/// </summary>
public sealed class Preset
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Link { get; set; }
    public List<PresetAccount> Accounts { get; set; } = new();
    /// <summary>Pinned to Home (at most <see cref="Presets.MaxFavorites"/>).</summary>
    public bool Favorite { get; set; }
    /// <summary>
    /// On: launching turns AFK mode on (anti-AFK, the watchdog's rejoining, alerts) and starts the saved macros.
    /// Off: just opens the windows, for playing on several accounts yourself.
    /// </summary>
    public bool Afk { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }
    /// <summary>The game's real name and icon, looked up from Roblox (refreshed weekly).</summary>
    public string Game { get; set; }
    public string Icon { get; set; }
    public DateTime MetaUtc { get; set; }
}

public sealed class PresetAccount
{
    public long UserId { get; set; }
    /// <summary>FPS the window opens at (0 = AFK FPS), and lowest graphics — same meaning as on the Accounts page.</summary>
    public int Fps { get; set; }
    public bool LowGraphics { get; set; }
    /// <summary>null = idle (anti-AFK only).</summary>
    public string MacroId { get; set; }
    public double Speed { get; set; } = 1.0;
    public bool Loop { get; set; } = true;
    public int LoopsPerTurn { get; set; } = 1;
    /// <summary>Start the macro once the window is in game.</summary>
    public bool RunMacro { get; set; }
}

static class Presets
{
    public const int MaxFavorites = 3;

    /// <summary>Snapshots the given game and accounts with each account's current FPS, graphics and macro.</summary>
    public static Preset Capture(Hub hub, string name, string link, IEnumerable<Account> accounts, Preset into = null)
    {
        var p = into ?? new Preset { Id = MacroLibrary.NewId(), CreatedUtc = DateTime.UtcNow };
        p.Name = name;
        if (p.Link != link) { p.Game = p.Icon = null; p.MetaUtc = default; }
        p.Link = link;
        p.Accounts = accounts.Select(a =>
        {
            var key = "acct:" + a.UserId;
            hub.Cfg.MacroAssignments.TryGetValue(key, out var m);
            bool hasMacro = m != null && hub.Macros.Get(m.MacroId) != null;
            return new PresetAccount
            {
                UserId = a.UserId,
                Fps = a.Fps,
                LowGraphics = a.LowGraphics,
                MacroId = hasMacro ? m.MacroId : null,
                Speed = hasMacro ? m.Speed : 1.0,
                Loop = !hasMacro || m.Loop,
                LoopsPerTurn = hasMacro ? m.LoopsPerTurn : 1,
                RunMacro = hasMacro && hub.Cfg.MacroRunKeys.Contains(key),
            };
        }).ToList();
        return p;
    }

    /// <summary>
    /// Puts the preset's account and macro settings in place (they stay that way, like changing them by hand)
    /// and returns the accounts to launch plus the macro windows to start once they're in game.
    /// </summary>
    public static (List<Account> Accounts, List<string> RunKeys) Apply(Hub hub, Preset p)
    {
        var accounts = new List<Account>();
        var runKeys = new List<string>();
        foreach (var pa in p.Accounts)
        {
            var acc = hub.Accounts.Accounts.FirstOrDefault(a => a.UserId == pa.UserId);
            if (acc == null) continue; // removed since the preset was saved
            accounts.Add(acc);
            acc.Fps = pa.Fps;
            acc.LowGraphics = pa.LowGraphics;

            var key = "acct:" + acc.UserId;
            if (pa.MacroId != null && hub.Macros.Get(pa.MacroId) != null)
            {
                if (!hub.Cfg.MacroAssignments.TryGetValue(key, out var a)) hub.Cfg.MacroAssignments[key] = a = new MacroAssignment();
                a.MacroId = pa.MacroId;
                a.Speed = pa.Speed;
                a.Loop = pa.Loop;
                a.LoopsPerTurn = Math.Max(1, pa.LoopsPerTurn);
                a.PinnedRect = null;
                if (pa.RunMacro && p.Afk) runKeys.Add(key);
            }
            else
            {
                hub.Cfg.MacroAssignments.Remove(key);
                hub.Cfg.MacroRunKeys.Remove(key);
            }
        }
        hub.Cfg.LastLink = p.Link;
        hub.Cfg.LastAccounts = accounts.Select(a => a.UserId).ToList();
        p.LastUsedUtc = DateTime.UtcNow;
        hub.SaveAccounts();
        hub.SaveConfig();
        return (accounts, runKeys);
    }
}
