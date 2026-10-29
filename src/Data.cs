using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AfkHub;

public sealed class Config
{
    public List<int> FpsPresets { get; set; } = new() { 5, 10, 15, 30, 60, 120, 240 };
    public int AfkFps { get; set; } = 15;
    public int NormalFps { get; set; }
    public bool AfkBeforeLaunch { get; set; } = true;

    public bool MultiInstance { get; set; } = true;

    public bool AntiAfk { get; set; } = true;
    public int AntiAfkMinutes { get; set; } = 15;
    public string AntiAfkKey { get; set; } = "Backslash";
    public bool WaitWhileBusy { get; set; } = true;

    public bool AlertOnDisconnect { get; set; } = true;
    public bool AutoRejoin { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    public string Theme { get; set; } = "midnight";
    /// <summary>The account the Home page greets and shows (0 = first account).</summary>
    public long MainAccountId { get; set; }
    /// <summary>Master switch: turns off every optional extra (avatar, friends, status, Discord, effects) at once.</summary>
    public bool LiteMode { get; set; }
    public bool ShowFriends { get; set; } = true;
    public bool ShowPresence { get; set; } = true;
    public bool ShowAvatar { get; set; } = true;
    /// <summary>After the first account joins a public game, send the rest into that same server.</summary>
    public bool JoinTogether { get; set; } = true;
    public List<RecentPlace> RecentPlaces { get; set; } = new();

    // Phone alerts (Discord webhook)
    public string WebhookUrl { get; set; }
    public bool WebhookDisconnects { get; set; } = true;
    public bool WebhookActions { get; set; } = true;
    public bool WebhookSummary { get; set; } = true;
    /// <summary>During AFK mode, post how every account is doing this often (0 = off).</summary>
    public int WebhookCheckinHours { get; set; } = 3;
    /// <summary>One Discord message that keeps editing itself with the AFK status.</summary>
    public bool WebhookLiveStatus { get; set; } = true;
    public bool LiveScreenshot { get; set; }
    public string LiveStatusMessageId { get; set; }

    // Overnight automation
    public bool StartWithWindows { get; set; }
    public bool AutoResume { get; set; } = true;
    /// <summary>AFK mode was on when the app last stopped without you pressing "I'm back" (e.g. a Windows Update restart).</summary>
    public bool ResumePending { get; set; }
    public List<string> ResumeMacroKeys { get; set; } = new();
    public bool FreezeRecovery { get; set; } = true;
    public int FreezeMinutes { get; set; } = 3;
    public int RejoinEveryHours { get; set; }
    public bool ScheduleOn { get; set; }
    public string ScheduleStart { get; set; } = "23:00";
    public string ScheduleStop { get; set; } = "08:00";
    public bool ScheduleLaunch { get; set; } = true;
    public bool ScheduleCloseRoblox { get; set; }
    public string ScheduleLastStart { get; set; }
    public string ScheduleLastStop { get; set; }

    public bool DiscordRpc { get; set; }
    public string DiscordAppId { get; set; }
    public bool DiscordOnlyWhileAfk { get; set; } = true;
    public bool DiscordShowGame { get; set; } = true;
    public bool AvatarEffects { get; set; } = true;
    public bool ReduceEffects { get; set; }
    public bool GuideDismissed { get; set; }
    /// <summary>The first-run answer: "play" (several accounts, by hand) or "afk" (kept in game while away); null = not asked yet.</summary>
    public string Mode { get; set; }
    public bool ShowMacros { get; set; } = true;
    /// <summary>Look for a new version on GitHub after starting (and twice a day).</summary>
    public bool AutoUpdateCheck { get; set; } = true;
    /// <summary>A version you pressed "Later" on; its banner stays hidden until an even newer one comes out.</summary>
    public string SkipUpdateVersion { get; set; }
    public bool HasLaunched { get; set; }

    public Dictionary<string, MacroAssignment> MacroAssignments { get; set; } = new();
    /// <summary>Windows whose macros are running, in turn order. They take turns because Roblox only reads the window in front.</summary>
    public List<string> MacroRunKeys { get; set; } = new();
    public bool MacroPauseOnInput { get; set; } = true;
    public bool MacroAutoResume { get; set; } = true;
    public int MacroResumeAfterSec { get; set; } = 60;
    public bool MacroPinWindows { get; set; } = true;

    public bool KeepAwake { get; set; } = true;
    public bool KeepScreenOn { get; set; } = true;
    /// <summary>Your graphics values from before a "lowest graphics" launch, put back when those windows close.</summary>
    public Dictionary<string, int> GraphicsBaseline { get; set; }

    public string RobloxAutostartBackup { get; set; }
    public List<SavedGame> SavedGames { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();
    public string LastLink { get; set; }
    public List<long> LastAccounts { get; set; } = new();
}

public sealed class SavedGame
{
    public string Name { get; set; }
    public string Link { get; set; }
    /// <summary>The game's real name and icon, looked up from Roblox (refreshed weekly).</summary>
    public string Game { get; set; }
    public string Icon { get; set; }
    public DateTime MetaUtc { get; set; }
    public override string ToString() => Name;
}

/// <summary>A game one of your windows joined, for the "Recently played" list.</summary>
public sealed class RecentPlace
{
    public long PlaceId { get; set; }
    public DateTime LastUtc { get; set; }
    public int Joins { get; set; }
    public string Game { get; set; }
    public string Icon { get; set; }
    public DateTime MetaUtc { get; set; }
}

public sealed class Account
{
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public long UserId { get; set; }
    public string Secret { get; set; }
    public DateTime AddedUtc { get; set; }
    /// <summary>FPS cap this account's window opens with; 0 = use the AFK/current FPS.</summary>
    public int Fps { get; set; }
    public bool LowGraphics { get; set; }
    public override string ToString() => DisplayName != null && DisplayName != Name ? $"{DisplayName} (@{Name})" : Name;
}

public sealed class AccountStore
{
    public List<Account> Accounts { get; set; } = new();
}

public sealed class SessionRecord
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int Presses { get; set; }
    public int Disconnects { get; set; }
    public int Rejoins { get; set; }
    public int MaxWindows { get; set; }
    public List<string> Accounts { get; set; } = new();
    /// <summary>Seconds each account actually spent in game this session (account label → seconds).</summary>
    public Dictionary<string, int> InGameSeconds { get; set; } = new();
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}

public sealed class StatsFile
{
    public List<SessionRecord> Sessions { get; set; } = new();
    public SessionRecord Current { get; set; }
}

static class Store
{
    /// <summary>A folder with this name next to AfkHub.exe makes the app portable: all its data lives there.</summary>
    public const string PortableFolderName = "AfkHub-data";
    public static readonly string PortableDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName);
    public static readonly string Dir = Directory.Exists(PortableDir) ? PortableDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AfkHub");
    public static bool IsPortable => Dir == PortableDir;

    /// <summary>Copies everything (settings, accounts, macros, stats) next to the exe so it travels with it.</summary>
    public static void MakePortable()
    {
        Directory.CreateDirectory(PortableDir);
        foreach (var f in new[] { "config.json", "accounts.json", "stats.json" })
            if (File.Exists(Path.Combine(Dir, f))) File.Copy(Path.Combine(Dir, f), Path.Combine(PortableDir, f), true);
        var macros = Path.Combine(Dir, "macros");
        if (Directory.Exists(macros))
        {
            Directory.CreateDirectory(Path.Combine(PortableDir, "macros"));
            foreach (var f in Directory.GetFiles(macros, "*.json")) File.Copy(f, Path.Combine(PortableDir, "macros", Path.GetFileName(f)), true);
        }
    }
    static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };

    public static T Load<T>(string name) where T : new()
    {
        var path = Path.Combine(Dir, name);
        if (!File.Exists(path)) return new T();
        try
        {
            return Json.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch
        {
            // Keep the unreadable file instead of silently overwriting it with defaults.
            File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            return new T();
        }
    }

    public static void Save(string name, object value)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Json.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}

/// <summary>Roblox login cookies, encrypted with Windows DPAPI so they only decrypt under this Windows account.</summary>
static class Secrets
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AfkHub.v1.roblosecurity");

    public static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string blob)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException)
        {
            throw new RobloxAuthException("This login was saved on a different PC or Windows user, so it can't be used here. Remove the account and add it again on this PC.");
        }
    }

    public static bool CanUnprotect(string blob)
    {
        try { Unprotect(blob); return true; } catch { return false; }
    }
}

static class FpsFile
{
    public static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");

    public const string Fps = "FramerateCap", SavedQuality = "SavedQualityLevel", GraphicsLevel = "GraphicsQualityLevel";

    /// <summary>Roblox's lowest graphics: manual quality level 1.</summary>
    public static readonly Dictionary<string, int> LowestGraphics = new() { [SavedQuality] = 1, [GraphicsLevel] = 1 };

    static Regex ValueRx(string name) => new($@"(<(int|token) name=""{name}"">)(-?\d+)(</\2>)");

    public static bool Exists => File.Exists(SettingsPath);

    // The dashboard asks every second; only re-read the file when it actually changed.
    static DateTime _cachedStamp;
    static int? _cachedFps;

    public static int? Read()
    {
        if (!Exists) return null;
        var stamp = File.GetLastWriteTimeUtc(SettingsPath);
        if (stamp == _cachedStamp) return _cachedFps;
        _cachedFps = ReadValues(Fps).TryGetValue(Fps, out var v) ? v : null;
        _cachedStamp = stamp;
        return _cachedFps;
    }

    public static Dictionary<string, int> ReadValues(params string[] names)
    {
        var result = new Dictionary<string, int>();
        if (!Exists) return result;
        var text = File.ReadAllText(SettingsPath);
        foreach (var name in names)
        {
            var m = ValueRx(name).Match(text);
            if (m.Success) result[name] = int.Parse(m.Groups[3].Value);
        }
        return result;
    }

    public static void Write(int fps) => WriteValues(new Dictionary<string, int> { [Fps] = fps });

    public static bool IsLocked => Exists && (File.GetAttributes(SettingsPath) & FileAttributes.ReadOnly) != 0;

    public static void SetLocked(bool locked)
    {
        if (!Exists) return;
        var attrs = File.GetAttributes(SettingsPath);
        File.SetAttributes(SettingsPath, locked ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly);
    }

    public static void WriteValues(IDictionary<string, int> values)
    {
        if (!Exists) throw new FileNotFoundException("Roblox settings file not found. Open Roblox once so it creates it.", SettingsPath);

        var bytes = File.ReadAllBytes(SettingsPath);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var utf8 = new UTF8Encoding(false);
        var text = utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));

        var updated = text;
        foreach (var kv in values)
        {
            var rx = ValueRx(kv.Key);
            if (!rx.IsMatch(updated)) throw new InvalidDataException($"{kv.Key} wasn't found in the Roblox settings file.");
            updated = rx.Replace(updated, m => m.Groups[1].Value + kv.Value + m.Groups[4].Value, 1);
        }
        if (updated == text) return;

        var backup = Path.Combine(Store.Dir, "GlobalBasicSettings_13.original.xml");
        if (!File.Exists(backup)) { Directory.CreateDirectory(Store.Dir); File.WriteAllBytes(backup, bytes); }

        var output = (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : new byte[0]).Concat(utf8.GetBytes(updated)).ToArray();

        bool wasLocked = IsLocked;
        if (wasLocked) SetLocked(false);
        try { File.WriteAllBytes(SettingsPath, output); }
        finally { if (wasLocked) SetLocked(true); }
    }
}

/// <summary>Roblox adds "RobloxPlayerBeta --launch-to-tray" to the Run key; that tray process holds the singleton locks from boot.</summary>
static class RobloxAutostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RobloxPlayerBeta";

    public static string Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static void Disable(Config cfg)
    {
        var value = Current();
        if (value == null) return;
        cfg.RobloxAutostartBackup = value;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(ValueName, false);
    }

    public static bool Enable(Config cfg)
    {
        if (string.IsNullOrEmpty(cfg.RobloxAutostartBackup)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.SetValue(ValueName, cfg.RobloxAutostartBackup);
        return true;
    }
}
