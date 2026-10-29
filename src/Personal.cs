using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AfkHub;

/// <summary>Everything that makes the app feel like yours: avatar renders, outfit, presence, game icons, accent color.</summary>
sealed class Personal
{
    public sealed class AvatarInfo
    {
        public string Headshot, Bust, Full;
        public string Accent, AccentInk;
        public List<OutfitItem> Outfit = new();
        public DateTime FetchedUtc;
    }

    public sealed class OutfitItem { public long Id; public string Name, Type, Thumb; }

    public sealed class PresenceInfo { public string Text, Kind, Game, GameIcon; public long PlaceId; }

    public sealed class FriendInGame
    {
        public long UserId, PlaceId;
        public string Name, Display, Headshot, Game, GameIcon, JobId;
        public bool Mine;
    }

    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };

    readonly Hub _hub;
    public Dictionary<long, AvatarInfo> Avatars { get; } = new();
    public Dictionary<long, PresenceInfo> Presence { get; } = new();
    readonly Dictionary<long, (string Name, string Icon)> _games = new(); // by universe id
    readonly Dictionary<long, long> _placeUniverse = new();
    public List<FriendInGame> FriendsInGame { get; private set; } = new();
    public int FriendCount { get; private set; }
    public bool FriendsLoaded { get; private set; }
    List<long> _friendIds;
    long _friendIdsFor;
    DateTime _friendIdsUtc, _lastFriends;
    bool _friendsBusy, _recentBusy;
    bool _refreshing, _presenceBusy, _favBusy;
    DateTime _lastPresence;

    public event Action Changed;

    public Personal(Hub hub)
    {
        _hub = hub;
        // Names saved by an earlier version may be garbled ("ðŸ¾"); re-fetch those.
        foreach (var f in hub.Cfg.SavedGames.Where(f => Garbled(f.Game))) { f.Game = null; f.MetaUtc = default; }
        foreach (var r in hub.Cfg.RecentPlaces.Where(r => Garbled(r.Game))) { r.Game = null; r.MetaUtc = default; }
    }

    static bool Garbled(string s) => s != null && (s.Contains("\u00F0\u0178") || s.Contains("\u00F0\u009F") || s.Contains("\u00C3") || s.Contains("\u00E2\u20AC"));

    public long MainAccountId =>
        _hub.Accounts.Accounts.Any(a => a.UserId == _hub.Cfg.MainAccountId) ? _hub.Cfg.MainAccountId
        : _hub.Accounts.Accounts.FirstOrDefault()?.UserId ?? 0;

    public AvatarInfo Get(long userId) => Avatars.TryGetValue(userId, out var a) ? a : null;

    /// <summary>Small picture for lists: the headshot (keeps an animated R15 face), falling back to the bust.</summary>
    public string SmallPicture(long userId) => Get(userId) is { } a ? a.Headshot ?? a.Bust : null;

    // ---------------------------------------------------------------- avatars

    public async Task RefreshAvatarsAsync(bool force = false)
    {
        if (_refreshing) return;
        var ids = _hub.Accounts.Accounts.Select(a => a.UserId).ToList();
        if (ids.Count == 0) return;
        if (!force && ids.All(id => Get(id) is { } a && DateTime.UtcNow - a.FetchedUtc < TimeSpan.FromMinutes(20) && a.Full != null)) return;
        _refreshing = true;
        try
        {
            // Fresh outfits can come back "Pending" while Roblox renders them; try a few times.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var head = await Thumbs("avatar-headshot", ids, "150x150");
                var bust = await Thumbs("avatar-bust", ids, "150x150");
                var full = await Thumbs("avatar", ids, "420x420");
                foreach (var id in ids)
                {
                    if (!Avatars.TryGetValue(id, out var a)) Avatars[id] = a = new AvatarInfo();
                    if (head.TryGetValue(id, out var h)) a.Headshot = h;
                    if (bust.TryGetValue(id, out var b)) a.Bust = b;
                    if (full.TryGetValue(id, out var f)) a.Full = f;
                    a.FetchedUtc = DateTime.UtcNow;
                }
                Changed?.Invoke();
                if (ids.All(id => Get(id) is { Bust: not null, Full: not null })) break;
                await Task.Delay(4000);
            }

            foreach (var id in ids)
                if (Get(id) is { } a && (a.Bust ?? a.Headshot) is { } pic)
                    (a.Accent, a.AccentInk) = await AccentFrom(pic);
            Changed?.Invoke();
        }
        catch { /* decoration only */ }
        finally { _refreshing = false; }
    }

    static async Task<Dictionary<long, string>> Thumbs(string kind, List<long> ids, string size)
    {
        var result = new Dictionary<long, string>();
        var body = await GetJson($"https://thumbnails.roblox.com/v1/users/{kind}?userIds={string.Join(",", ids)}&size={size}&format=Png&isCircular=false");
        foreach (var item in Items(body, "data"))
            if (item.TryGetValue("state", out var st) && (st as string) == "Completed" && item["imageUrl"] is string url)
                result[Convert.ToInt64(item["targetId"])] = url;
        return result;
    }

    async Task RefreshOutfitAsync(long userId)
    {
        if (userId == 0 || Get(userId) is not { } a) return;
        var wearing = await GetJson($"https://avatar.roblox.com/v1/users/{userId}/currently-wearing");
        var ids = wearing != null && wearing.TryGetValue("assetIds", out var arr) && arr is System.Collections.ArrayList list
            ? list.Cast<object>().Select(Convert.ToInt64).Distinct().Take(24).ToList() : new List<long>();
        if (ids.Count == 0) { a.Outfit = new(); return; }

        var thumbs = new Dictionary<long, string>();
        foreach (var item in Items(await GetJson($"https://thumbnails.roblox.com/v1/assets?assetIds={string.Join(",", ids)}&returnPolicy=PlaceHolder&size=110x110&format=Png&isCircular=false"), "data"))
            if (item["imageUrl"] is string url) thumbs[Convert.ToInt64(item["targetId"])] = url;

        // Item names need a logged-in request (catalog details); use this account's own login if it works here.
        var names = new Dictionary<long, (string Name, string Type)>();
        var acc = _hub.Accounts.Accounts.FirstOrDefault(x => x.UserId == userId);
        if (acc != null && Secrets.CanUnprotect(acc.Secret))
        {
            try
            {
                var payload = Json.Serialize(new { items = ids.Select(id => new { itemType = "Asset", id }).ToArray() });
                var text = await RobloxApi.PostWithCsrf(Secrets.Unprotect(acc.Secret), "https://catalog.roblox.com/v1/catalog/items/details", payload);
                foreach (var item in Items(Json.Deserialize<Dictionary<string, object>>(text), "data"))
                    names[Convert.ToInt64(item["id"])] = (item.TryGetValue("name", out var n) ? n as string : null,
                        item.TryGetValue("assetType", out var t) ? AssetTypeName(Convert.ToInt32(t)) : null);
            }
            catch { }
        }
        // Body parts and animations aren't really "clothes"; list worn items first.
        a.Outfit = ids.Select(id => new OutfitItem
        {
            Id = id,
            Thumb = thumbs.TryGetValue(id, out var th) ? th : null,
            Name = names.TryGetValue(id, out var nm) ? nm.Name : null,
            Type = names.TryGetValue(id, out var nt) ? nt.Type : null,
        }).Where(o => o.Thumb != null && o.Type is not ("Animation" or "Body part")).ToList();
    }

    static string AssetTypeName(int t) => t switch
    {
        8 => "Hat", 41 => "Hair", 42 => "Face accessory", 43 => "Neck", 44 => "Shoulder", 45 => "Front", 46 => "Back", 47 => "Waist",
        2 => "T-shirt", 11 => "Shirt", 12 => "Pants", 18 => "Face", 64 => "T-shirt", 65 => "Shirt", 66 => "Pants", 67 => "Jacket",
        68 => "Sweater", 69 => "Shorts", 70 => "Shoes", 71 => "Shoes", 72 => "Dress", 79 => "Head", 76 => "Eyebrows", 77 => "Eyelashes",
        >= 48 and <= 56 => "Animation", 17 or 27 or 28 or 29 or 30 or 31 => "Body part",
        _ => "Item",
    };

    /// <summary>Picks the most vivid, common color in the avatar picture and makes it UI-friendly.</summary>
    static async Task<(string, string)> AccentFrom(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var src = new Bitmap(new MemoryStream(bytes));
            using var small = new Bitmap(src, new Size(32, 32));
            var buckets = new double[24];
            var sumR = new double[24]; var sumG = new double[24]; var sumB = new double[24];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    var c = small.GetPixel(x, y);
                    if (c.A < 200) continue;
                    float s = c.GetSaturation(), v = Math.Max(c.R, Math.Max(c.G, c.B)) / 255f;
                    if (s < 0.25f || v < 0.25f) continue; // skip greys, blacks, whites
                    int b = (int)(c.GetHue() / 15) % 24;
                    double w = s * v;
                    buckets[b] += w; sumR[b] += c.R * w; sumG[b] += c.G * w; sumB[b] += c.B * w;
                }
            int best = Array.IndexOf(buckets, buckets.Max());
            if (buckets[best] < 0.5) return (null, null); // a mostly grey/black avatar: no accent
            var avg = Color.FromArgb((int)(sumR[best] / buckets[best]), (int)(sumG[best] / buckets[best]), (int)(sumB[best] / buckets[best]));
            // Lift it to a bright, saturated version so it reads well on dark backgrounds.
            var accent = FromHsv(avg.GetHue(), Math.Max(0.55, avg.GetSaturation()), 0.95);
            double lum = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255;
            return ($"#{accent.R:X2}{accent.G:X2}{accent.B:X2}", lum > 0.55 ? "#0b0c0f" : "#ffffff");
        }
        catch { return (null, null); }
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // ---------------------------------------------------------------- presence (where each account is)

    public async Task RefreshPresenceAsync()
    {
        if (_presenceBusy || DateTime.UtcNow - _lastPresence < TimeSpan.FromSeconds(40)) return;
        _presenceBusy = true;
        _lastPresence = DateTime.UtcNow;
        try
        {
            foreach (var acc in _hub.Accounts.Accounts.ToList())
            {
                if (!Secrets.CanUnprotect(acc.Secret)) continue;
                try
                {
                    var text = await RobloxApi.PostWithCsrf(Secrets.Unprotect(acc.Secret), "https://presence.roblox.com/v1/presence/users",
                        Json.Serialize(new { userIds = new[] { acc.UserId } }));
                    var p = Items(Json.Deserialize<Dictionary<string, object>>(text), "userPresences").FirstOrDefault();
                    if (p == null) continue;
                    int type = Convert.ToInt32(p["userPresenceType"]);
                    long universe = p.TryGetValue("universeId", out var u) && u != null ? Convert.ToInt64(u) : 0;
                    long place = p.TryGetValue("placeId", out var pl) && pl != null ? Convert.ToInt64(pl) : 0;
                    var info = new PresenceInfo { PlaceId = place };
                    if (type == 2 && universe > 0)
                    {
                        var g = await GameInfo(universe);
                        info.Game = g.Name ?? (p.TryGetValue("lastLocation", out var loc) ? loc as string : null);
                        info.GameIcon = g.Icon;
                    }
                    (info.Text, info.Kind) = type switch
                    {
                        2 => (info.Game != null ? $"In {info.Game}" : "In a game", "good"),
                        1 => ("Online", "accent"),
                        3 => ("In Roblox Studio", "accent"),
                        _ => ("Offline", "neutral"),
                    };
                    Presence[acc.UserId] = info;
                }
                catch { }
            }
            Changed?.Invoke();
        }
        finally { _presenceBusy = false; }
    }

    // ---------------------------------------------------------------- game names + icons for favorites

    public async Task<(string Name, string Icon)> GameInfo(long universeId)
    {
        if (_games.TryGetValue(universeId, out var g)) return g;
        string name = null, icon = null;
        var games = await GetJson($"https://games.roblox.com/v1/games?universeIds={universeId}");
        name = Items(games, "data").FirstOrDefault()?["name"] as string;
        var icons = await GetJson($"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=150x150&format=Png&isCircular=false");
        icon = Items(icons, "data").FirstOrDefault()?["imageUrl"] as string;
        return _games[universeId] = (name, icon);
    }

    /// <summary>Fills in the game name and icon for saved favorites and presets (refreshed weekly; Roblox's image links expire).</summary>
    public async Task RefreshFavoritesAsync()
    {
        if (_favBusy) return;
        _favBusy = true;
        bool changed = false;
        try
        {
            static bool Fresh(string game, string icon, DateTime meta) => icon != null && game != null && DateTime.UtcNow - meta < TimeSpan.FromDays(7);
            foreach (var fav in _hub.Cfg.SavedGames.ToList())
            {
                if (Fresh(fav.Game, fav.Icon, fav.MetaUtc) || await LinkGame(fav.Link) is not { } g) continue;
                (fav.Game, fav.Icon, fav.MetaUtc) = (g.Name, g.Icon, DateTime.UtcNow);
                changed = true;
            }
            foreach (var p in _hub.Cfg.Presets.ToList())
            {
                if (Fresh(p.Game, p.Icon, p.MetaUtc) || await LinkGame(p.Link) is not { } g) continue;
                (p.Game, p.Icon, p.MetaUtc) = (g.Name, g.Icon, DateTime.UtcNow);
                changed = true;
            }
            if (changed) { _hub.SaveConfig(); Changed?.Invoke(); }
        }
        finally { _favBusy = false; }
    }

    /// <summary>The game behind a link (share links are resolved with one of your logins), or null.</summary>
    async Task<(string Name, string Icon)?> LinkGame(string link)
    {
        try
        {
            var target = GameLink.Parse(link);
            if (target.PlaceId == 0 && target.ShareCode != null)
            {
                var acc = _hub.Accounts.Accounts.FirstOrDefault(a => Secrets.CanUnprotect(a.Secret));
                if (acc == null) return null;
                await RobloxApi.ResolveShareLink(Secrets.Unprotect(acc.Secret), target);
            }
            if (target.PlaceId == 0) return null;
            var g = await PlaceInfo(target.PlaceId);
            return g.Name == null ? null : g;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- places (recently played, Discord)

    public async Task<(string Name, string Icon)> PlaceInfo(long placeId)
    {
        if (placeId <= 0) return (null, null);
        if (!_placeUniverse.TryGetValue(placeId, out var uni))
        {
            var u = await GetJson($"https://apis.roblox.com/universes/v1/places/{placeId}/universe");
            if (u == null || !u.TryGetValue("universeId", out var uv) || uv == null) return (null, null);
            _placeUniverse[placeId] = uni = Convert.ToInt64(uv);
        }
        return await GameInfo(uni);
    }

    /// <summary>Cached name/icon for a place if already looked up (no network).</summary>
    public (string Name, string Icon)? CachedPlace(long placeId) =>
        _placeUniverse.TryGetValue(placeId, out var uni) && _games.TryGetValue(uni, out var g) ? g : null;

    public async Task RefreshRecentAsync()
    {
        if (_recentBusy) return;
        _recentBusy = true;
        bool changed = false;
        try
        {
            foreach (var r in _hub.Cfg.RecentPlaces.ToList())
            {
                if (r.Icon != null && r.Game != null && DateTime.UtcNow - r.MetaUtc < TimeSpan.FromDays(7)) continue;
                var g = await PlaceInfo(r.PlaceId);
                if (g.Name == null) continue;
                r.Game = g.Name; r.Icon = g.Icon; r.MetaUtc = DateTime.UtcNow;
                changed = true;
            }
            if (changed) { _hub.SaveConfig(); Changed?.Invoke(); }
        }
        catch { }
        finally { _recentBusy = false; }
    }

    // ---------------------------------------------------------------- friends in game (join their server)

    public async Task RefreshFriendsAsync(bool force = false)
    {
        if (_friendsBusy || (!force && DateTime.UtcNow - _lastFriends < TimeSpan.FromSeconds(60))) return;
        long mainId = MainAccountId;
        var acc = _hub.Accounts.Accounts.FirstOrDefault(a => a.UserId == mainId);
        if (acc == null || !Secrets.CanUnprotect(acc.Secret)) return;
        _friendsBusy = true;
        _lastFriends = DateTime.UtcNow;
        try
        {
            string cookie = Secrets.Unprotect(acc.Secret);
            if (_friendIds == null || _friendIdsFor != mainId || DateTime.UtcNow - _friendIdsUtc > TimeSpan.FromMinutes(10))
            {
                var ids = new List<long>();
                string cursor = null;
                for (int page = 0; page < 10; page++)
                {
                    var text = await RobloxApi.GetWithCookie(cookie,
                        $"https://friends.roblox.com/v1/users/{mainId}/friends/find?limit=50" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""));
                    var body = Json.Deserialize<Dictionary<string, object>>(text);
                    ids.AddRange(Items(body, "PageItems").Select(x => Convert.ToInt64(x["id"])));
                    cursor = body.TryGetValue("NextCursor", out var nc) ? nc as string : null;
                    if (string.IsNullOrEmpty(cursor)) break;
                }
                _friendIds = ids;
                _friendIdsFor = mainId;
                _friendIdsUtc = DateTime.UtcNow;
            }
            FriendCount = _friendIds.Count;

            // Who's in a game right now (with their server, if their privacy settings share it).
            var playing = new List<FriendInGame>();
            foreach (var chunk in _friendIds.Select((id, i) => (id, i)).GroupBy(x => x.i / 50, x => x.id))
            {
                var text = await RobloxApi.PostWithCsrf(cookie, "https://presence.roblox.com/v1/presence/users",
                    Json.Serialize(new { userIds = chunk.ToArray() }));
                foreach (var p in Items(Json.Deserialize<Dictionary<string, object>>(text), "userPresences"))
                {
                    if (Convert.ToInt32(p["userPresenceType"]) != 2) continue;
                    long uni = p.TryGetValue("universeId", out var u) && u != null ? Convert.ToInt64(u) : 0;
                    long place = p.TryGetValue("placeId", out var pl) && pl != null ? Convert.ToInt64(pl) : 0;
                    var f = new FriendInGame
                    {
                        UserId = Convert.ToInt64(p["userId"]),
                        PlaceId = place,
                        JobId = p.TryGetValue("gameId", out var g) ? g as string : null,
                        Game = p.TryGetValue("lastLocation", out var loc) ? loc as string : null,
                    };
                    if (uni > 0) { var info = await GameInfo(uni); f.Game = info.Name ?? f.Game; f.GameIcon = info.Icon; if (place > 0) _placeUniverse[place] = uni; }
                    playing.Add(f);
                }
            }

            if (playing.Count > 0)
            {
                var ids = playing.Select(f => f.UserId).ToList();
                var users = await RobloxApi.PostWithCsrf(cookie, "https://users.roblox.com/v1/users", Json.Serialize(new { userIds = ids, excludeBannedUsers = true }));
                foreach (var u in Items(Json.Deserialize<Dictionary<string, object>>(users), "data"))
                {
                    var f = playing.FirstOrDefault(x => x.UserId == Convert.ToInt64(u["id"]));
                    if (f != null) { f.Name = u["name"] as string; f.Display = u.TryGetValue("displayName", out var dn) ? dn as string : null; }
                }
                var heads = await Thumbs("avatar-headshot", ids, "150x150");
                foreach (var f in playing) f.Headshot = heads.TryGetValue(f.UserId, out var h) ? h : null;
            }
            var mine = new HashSet<long>(_hub.Accounts.Accounts.Select(a => a.UserId));
            foreach (var f in playing) f.Mine = mine.Contains(f.UserId);
            FriendsInGame = playing.OrderByDescending(f => f.Mine).ThenBy(f => f.Game).ThenBy(f => f.Display).ToList();
            FriendsLoaded = true;
            Changed?.Invoke();
        }
        catch { /* try again next time */ }
        finally { _friendsBusy = false; }
    }

    // ---------------------------------------------------------------- helpers

    static async Task<Dictionary<string, object>> GetJson(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            return Json.Deserialize<Dictionary<string, object>>(await RobloxApi.ReadText(resp));
        }
        catch { return null; }
    }

    static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> body, string key) =>
        body != null && body.TryGetValue(key, out var v) && v is System.Collections.ArrayList list
            ? list.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
}
